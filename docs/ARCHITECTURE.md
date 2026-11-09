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
               ─ POST /public/me/find-more                 "Find more photos of me" (all references)
               ─ POST /public/me/matches/{photoId}/confirm|reject   "That's me" / "Not me"
               ─ POST /public/me/selfie                    extra selfie (max 3)
               ─ POST /public/photos/{id}/download         → signed original URL
               ─ POST /public/me/download-all → GET /public/zip?ticket=…   streamed ZIP (no temp files)
               ─ DELETE /public/me                         guest, selfie file, embeddings, matches
```
Admin: `/events/{id}/share-links` (Guest + Full in Phase 1), `/share-links/{id}/pin`, `qr.png`, `poster.png?size=a4|a5`
(rendered with bundled OFL fonts), `/events/{id}/guests`, `PUT /events/{id}/cover`.

Match Review: `GET /guests/{id}/matches` (selfies + every matched face with score and box), `PATCH /matches/{id}`
`{status: Confirmed|Rejected}`, `GET /guest-references/{id}/image` (selfie, admin only, `no-store`).
Thresholds: `GET /events/{id}/face-settings/preview?threshold=` (histogram), `POST /events/{id}/rematch` (Hangfire job).

**Matching runs in both directions** (see `AI-PIPELINE.md` §4–4d): forward (one guest × all faces) on
registration / "Find more" / rematch, reverse (one new photo × all guests) at the end of every face job, so the
pipeline is now `Uploaded → Resized → FacesDetected → Matched`.

## 7b. Live updates & notifications (Phase 2b)

```
 photo job (Hangfire, inside the api process)
   └─ after face detection + reverse match
        ├─ IRealtimeNotifier ──► SignalR hub /hubs/events
        │     admin:{eventId}    "PhotoProcessed"   → progress bar, photos grid refresh (no polling)
        │     viewers:{eventId}  "PhotoAdded"       → Full gallery: "3 new photos — tap to see"
        │     guest:{guestId}    "NewMatchesForYou" → guest gallery banner
        └─ IGuestNotifier (NotifyJobs) ──► debounced Hangfire job, max 1 per guest per 10 min
              ├─ Web Push (WebPush NuGet, VAPID keys from .env) → phone notification, even when closed
              └─ email (MailKit, SMTP from .env)                → only if the guest opted in
```
- **Hub auth**: the admin's cookie; guests send their JWT as `?access_token=` (WebSockets can't carry headers),
  accepted only on `/hubs`. nginx doesn't access-log `/hubs` so tokens never land in logs.
- **"Notify me"**: `PUT /public/me/notifications {subscription, email}` stores the browser's PushSubscription
  (an https endpoint at Google/Mozilla/Apple + keys) and/or the email opt-in; `DELETE` turns both off.
  A 404/410 from the push service means the phone unsubscribed → the subscription is dropped.
- **PWA**: Angular service worker (`ngsw-config.json`) + `manifest.webmanifest`: installable ("Add to Home
  Screen"), shows push notifications, opens the guest's gallery on tap. `/` sends a guest who installed the app
  to their last gallery. The worker never handles `/api`, `/media`, `/hubs`, `/hangfire`.
- **Settings** (`/api/v1/settings`): read-only status, "Send test email", "Generate VAPID keys" (shown once,
  never stored — paste into `.env`).

## 7c. Link types, album picks, watermark, expiry (Phase 2c)

| Link | Who | Sees | Form |
|---|---|---|---|
| **Guest** | guests (QR on tables) | only photos they're in (selfie) | fields you choose + consent |
| **VIP** | family, close friends | only photos they're in (selfie) | none — consent tick only |
| **Full** | everyone you trust | whole gallery by collection | optional |
| **Selection** | the couple | whole gallery + ❤ hearts, "37 / 40", comments, Submit (locks) | optional (name = "selected by") |

- **Selections** (`Selection`, `SelectionSubmission` tables): `POST/DELETE /public/selections/{photoId}`,
  `PUT …/comment`, `POST /public/selections/submit`. The limit and the lock are enforced by the API (409).
  Admin: `GET /events/{id}/selections`, `…/selections/export?format=names|csv|zip`, `POST /share-links/{id}/selection/unlock`.
  `names` = one comma-separated line without extensions → Lightroom: Library → Filter → Text → Filename → Contains.
  CSV fields starting with `= + - @` are prefixed with `'` (no Excel formula injection).
- **Watermark**: text drawn bottom-right on the `web` image only (thumbs and originals stay clean), at resize time.
  Changing it → "Rebuild images" re-renders `web`/`thumb` from the originals in the background (faces untouched).
- **Branding**: theme colour + a title font from a fixed list (validated by the API, loaded from Google Fonts).
- **Expiry**: after `Event.ExpiresAt` all guest links close at once (`Event.IsOpen`); an hourly Hangfire job
  (`PurgeJobs`) deletes guests, selfie files, reference embeddings, matches and every face embedding of the event,
  and sets `PurgedAt`. Photos, collections and album picks stay.

## 7d. TV slideshow & hot-folder uploader (Phase 2e)

- **`/tv/{token}`** (Angular `tv/tv-page.ts`): signs in as an anonymous Full-link viewer, loads all photos of the
  chosen collection, cross-fades with a slow zoom, keyboard/remote control, Screen Wake Lock, re-joins SignalR
  (`PhotoAdded` → shown next) and refreshes the list every 20 min because signed media URLs live one hour.
- **`backend/tools/HotFolderUploader`**: .NET console app. FileSystemWatcher + 30 s rescan → "stable" check (size
  unchanged 2 s and file openable) → the same chunked upload API as the web uploader → local state file.
  Auth: `X-Api-Key` = `UPLOADER_API_KEY` → role `Uploader`, accepted only by the `Upload` policy (upload endpoints +
  `GET /api/v1/uploader/targets`). Key compared in constant time; empty/short key = feature off.

## 7e. Culling report (Phase 3a)

- face-service `/v1/analyze` also returns `ear` per face and `sharpness` + `phash` per photo (`app/quality.py`).
- `PhotoJobs.DetectFacesAsync` → `Culling.Apply` fills `Photo.BlurScore` (sharpest face), `Sharpness`,
  `PerceptualHash`, `ClosedEyesCount`, `Face.EyeAspectRatio` → `BurstGrouper.AssignAsync` (advisory lock per
  collection, transitive merge, `IsBurstBest` / `BurstPickedByAdmin`).
- `PhotoJobs.AnalyzeQualityAsync` (queue `faces`) = backfill without touching faces.
- API: `GET events/{id}/photos?filter=Blurry|EyesClosed|Duplicates|Highlights|Hidden`, `GET …/photos/culling`,
  `POST …/photos/analyze-quality`, `POST …/photos/hide-burst-extras`, `POST photos/{id}/keep-in-burst`.

## 7f. Highlight reels (Phase 3b)

```
guest: POST /api/v1/public/me/reel ── ReelService: rules (Guest/VIP link, ≥ 3 photos, 1 at a time, 3 per 24 h)
        │                                 + ReelPlanner.Pick → Reel row (Queued, plan as JSON)
        ▼  Hangfire queue "video"
reel-worker container (same image as api + ffmpeg; Processing__VideoWorkers=1, other pools 0)
   ReelJobs.RenderAsync → FfmpegReelRenderer (frames → ffmpeg) → events/{e}/selfies/{guest}/reel-{id}.mp4
        │  queues ReelJobs.AnnounceAsync on "default" (the worker has no SignalR clients)
        ▼
api → SignalR "ReelUpdated" → guest's reel card reloads (also polls every 5 s while rendering)
```
- The MP4 lives in the guest's own folder, so deleting the guest or the expiry purge removes it. Older reels are
  deleted when a new one is ready (rows stay as `Replaced` for the daily limit).
- Served by `MediaController` as `PhotoVariant.Reel` (signed URL, range requests, `?download=1`).
- Settings shows the music tracks and whether a Hangfire server on queue `video` is alive.
- `backend/Dockerfile` stages: `runtime` (api) and `reel` (runtime + ffmpeg, ~580 MB more).

## 7g. Highlights strip (Phase 3c)

- `GET /api/v1/public/highlights` → stories per collection (`IsHighlight`, not hidden, collection not hidden).
  Full/Selection links see all; Guest/VIP links see all only if `Event.HighlightsForAllGuests` (default on),
  otherwise only highlights they are matched in.
- Admin: `GET events/{id}/photos/highlight-suggestions` (sharp, people, eyes open, burst keeper, not look-alike
  ≤ 16 bits of an already suggested photo, round-robin over collections), `POST events/{id}/photos/highlights`
  (bulk mark), `PUT events/{id}/highlight-settings`.
- Angular `guest/highlights/highlights-strip.ts` (bubbles + fullscreen viewer; navigation in `story-cursor.ts`).

## 8. Configuration

All secrets via `.env` → environment variables → ASP.NET Core configuration
(`FaceService__BaseUrl` ⇒ `FaceService:BaseUrl`). `appsettings.json` holds only local-dev defaults.
