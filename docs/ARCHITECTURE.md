# ShaadiLens — Architecture

> Status: Phase 0 (scaffold). Sections marked *(Phase N)* describe what's coming.

## 1. Containers

```
 phone / laptop ──HTTPS──► Cloudflare Tunnel (free) ──► web :8080
                                                         │
                         ┌───────────────────────────────┘
                         ▼
   ┌──────────────── web (nginx) ────────────────┐
   │  /            → Angular SPA (static files)   │
   │  /api /health /media /swagger /hangfire      │──► api :8080 (.NET 10)
   │  /hubs (WebSocket)                           │       │        │
   └──────────────────────────────────────────────┘       │        │ HTTP (internal)
                                                          ▼        ▼
                                          postgres :5432        face-service :8000
                                          (pgvector)            (Python, InsightFace)
                                                          │
                                                   photos volume (/data/photos)
```

| Service | Image | Public? | Purpose |
|---|---|---|---|
| `web` | nginx + Angular build | **yes** (only this) | Serves the SPA and reverse-proxies API traffic so browser + API share one origin (no CORS). |
| `api` | .NET 10 ASP.NET Core | no | REST API, SignalR, Hangfire jobs *(Phase 1)*, media serving. |
| `face-service` | Python 3.11 + FastAPI + InsightFace | no | Stateless: image in → faces + 512-d embeddings out. |
| `postgres` | `pgvector/pgvector:pg16` | no | All data, including face embeddings (`vector(512)`) and Hangfire queues *(Phase 1)*. |

In dev, `docker-compose.override.yml` additionally exposes api (5080), face-service (8000) and postgres (5433) on `127.0.0.1` only.

## 2. Backend layers (Clean Architecture)

```
ShaadiLens.Api            controllers, SignalR hubs, auth, Program.cs (composition root)
   │ references
   ├──► ShaadiLens.Application    use cases, DTOs, interfaces (IFaceEngine, IStorageService, INotifier)
   │        │ references
   │        └──► ShaadiLens.Domain     entities, enums, rules — depends on nothing
   └──► ShaadiLens.Infrastructure  implementations: EF Core, face-service HTTP client, storage, email, push
            └──► Application
```

**Why:** use cases depend only on interfaces. Example: `IFaceEngine` (Application) is implemented by
`FaceServiceClient` (Infrastructure). If we later replace Python with a pure-.NET ONNX engine, only
Infrastructure changes.

### Key pieces in Phase 0
| File | What it teaches |
|---|---|
| `Infrastructure/DependencyInjection.cs` | One `AddInfrastructure()` extension wires all infrastructure. Typed `HttpClient` for face-service via `IHttpClientFactory`. |
| `Infrastructure/FaceService/FaceServiceClient.cs` | Multipart upload to Python; snake_case JSON → C# records via `JsonNamingPolicy.SnakeCaseLower`. |
| `Api/Program.cs` | Serilog, ProblemDetails, OpenAPI + Swagger UI, two health endpoints. |
| `Api/Infrastructure/FaceServiceExceptionHandler.cs` | `IExceptionHandler`: face-service errors → 400/413/502 ProblemDetails. |

### Health checks
- `GET /health/live` — runs **no** checks; answers 200 if the process is up. Docker uses this, so the API
  isn't killed while face-service is still loading its model.
- `GET /health` — Postgres (`SELECT 1`) + face-service (`GET /health`). Returns 503 with JSON details if any fail.

## 3. Frontend

Angular 22, standalone components, signals, zoneless change detection, Angular Material 3.

```
src/app/
  core/     api urls + models (shared by everything; services/interceptors in Phase 1)
  shared/   reusable UI (api-status, not-found)
  admin/    /admin/**  — lazy-loaded; layout + dashboard
  guest/    /g/:token  — lazy-loaded; mobile-first landing
```

Lazy loading means guests on mobile data never download admin code. All HTTP calls use **relative URLs**
(`/api/...`); nginx (Docker) or `proxy.conf.json` (`ng serve`) routes them to the API.

## 4. Data model (EF Core, `Infrastructure/Persistence/AppDbContext.cs`)

```
Event ─┬─ Collection (Haldi, Mehendi, …)
       ├─ Photo ── Face (box, landmarks, Embedding vector(512) ← HNSW index)
       ├─ ShareLink (token, PIN hash, link type, download flags)
       └─ Guest ─┬─ GuestReference (Embedding vector(512): selfies, confirmed faces)
                 └─ GuestMatch (guest × photo, best face, score, Auto/Probable/Confirmed/Rejected)
UploadSession (chunked upload in progress) · AuditLog
```
- `FaceSettings` (thresholds) is an **owned type**: stored as columns of the `Events` table.
- Deleting an Event cascades to everything; the files are deleted from disk too.
- Migrations live in `Infrastructure/Persistence/Migrations` and are applied automatically at API startup
  (`DatabaseInitializer`), which also seeds a **Demo Wedding** with the 5 usual collections.

## 5. Photo upload → processing

```
Browser ── POST /events/{id}/uploads ─────────► UploadSession (chunkSize 8 MB, totalChunks)
        ── PUT  /uploads/{id}/chunks/{n} ×N ──► tmp/uploads/{id}/0000n.part   (any order, re-sendable)
        ── GET  /uploads/{id} ────────────────► which chunks arrived  (resume after a dropped connection)
        ── POST /uploads/{id}/complete ───────► join + SHA-256 → duplicate? → Photo row + original file
                                                   │ Hangfire (Postgres-backed queue)
                                                   ▼
                        queue "default" ─ PhotoJobs.PrepareAsync      ~0.3 s
                          HEIC→JPEG · EXIF date/camera · auto-orient · sRGB · strip metadata
                          web.jpg 2048px q82 · thumb.jpg 400px q75          Status = Resized
                                                   ▼
                        queue "faces"   ─ PhotoJobs.DetectFacesAsync  ~0.4 s + 0.4 s/face
                          web.jpg → face-service → filter (MinFaceSizePx, MinDetScore)
                          → Face rows with embeddings                       Status = FacesDetected
```
- **Two queues** so thumbnails appear in ~1 s even while face detection is busy; `FACE_WORKERS` sets the
  face pool size separately.
- **Idempotent + retried**: re-running a job replaces the faces; Hangfire retries 3×, then the photo is
  `Failed` with the error. Unreadable files fail immediately. "Reprocess" re-queues one photo.
- **Duplicates**: unique index `(EventId, ContentHash)`; uploading the same file again returns `"duplicate"`.
- The original file is kept byte-for-byte; web/thumb have **no EXIF/GPS** (guests never see locations).
- Dashboard: `/hangfire` (admin only) shows queues, retries and failures.

**Image library:** Magick.NET (ImageMagick, Apache 2.0) instead of the spec's ImageSharp. ImageSharp versions
before 3.2.0 have high-severity decoder CVEs, and 3.2.0+ requires a registered licence key for Release builds.
Magick.NET needs no key and also decodes HEIC.

## 6. Auth & media

| Who | How | Why |
|---|---|---|
| Admin | `POST /api/v1/auth/login` (ADMIN_EMAIL/PASSWORD from `.env`) → JWT in an **HttpOnly, SameSite=Strict cookie** `sl_admin` | JS can't read it (XSS-safe); works for `/hangfire` and plain download links |
| Guest | link token (+PIN) → register → 30-day JWT (`eid`, `lid`, `gid`, `ltype` claims) sent as `Authorization: Bearer` | one browser may hold several event links |
| Tickets | short JWTs with their own audience: "PIN entered" (2 h), "may download this ZIP" (10 min) | plain links/forms can't carry a Bearer header; a ticket can never be used as a login |

**Guest permission rule** (`GuestAccessService`): Guest links see only photos they're matched in; Full links see
every *visible* photo (event published, link active & unexpired, photo processed, photo + collection not hidden).
This is re-checked on every request, so closing a link or "Delete my data" ends existing sessions immediately.

**PIN brute force**: 5 *wrong* PINs per IP per link → 15-minute block (`PinAttemptLimiter`). Counting only failures
matters at a wedding: hundreds of guests share the venue Wi-Fi's single IP and the same QR code. PINs are stored
as PBKDF2 hashes, so the admin sees a PIN only once (when generated) and can regenerate it.

**Signed media URLs** — `/media/{photoId}/{thumb|web|original}?exp=…&sig=…`. The API only hands out a URL after
checking the caller may see the photo; the HMAC-SHA256 signature (key `MEDIA_SIGNING_KEY`) stops anyone editing
the photo id or variant. Expiry is rounded up to the next hour so URLs stay identical within an hour and the
browser cache works (`Cache-Control: private, immutable`).

## 7. Guest flow

```
QR → /g/{token} ─ GET  /public/links/{token}               branding, PIN?, fields, selfie needed?
               ─ POST /public/links/{token}/verify-pin     → PIN ticket
               ─ POST /public/links/{token}/register       form + consent + selfie (multipart)
                    selfie → normalise (Magick) → face-service /v1/selfie → 422 "Move closer" …
                    → Guest + GuestReference + selfie file → pgvector search → GuestMatch rows
                    ← guest JWT
               ─ GET  /public/me, /me/photos, /me/probable, /collections, /gallery (Full links)
               ─ POST /public/me/refresh                   "Check for new photos"
               ─ POST /public/photos/{id}/download         → signed original URL
               ─ POST /public/me/download-all → GET /public/zip?ticket=…   streamed ZIP (no temp files)
               ─ DELETE /public/me                         guest, selfie file, embeddings, matches
```
Admin: `/events/{id}/share-links` (Guest + Full in Phase 1), `/share-links/{id}/pin`, `qr.png`, `poster.png?size=a4|a5`
(rendered with bundled OFL fonts), `/events/{id}/guests`, `PUT /events/{id}/cover`.

Phase 2 adds the reverse direction for new uploads (new Face → all GuestReferences) + notifications.
See `AI-PIPELINE.md`.

## 8. Configuration

All secrets via `.env` → environment variables → ASP.NET Core configuration
(`FaceService__BaseUrl` ⇒ `FaceService:BaseUrl`). `appsettings.json` holds only local-dev defaults.
