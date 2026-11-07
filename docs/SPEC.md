# PROMPT FOR CLAUDE — "ShaadiLens": self-hosted AI wedding photo sharing (.NET + Angular)

## 0. Role and working rules

You are a senior full-stack engineer (.NET + Angular + applied computer vision). Build a **personal, self-hosted wedding photo-sharing web app** inspired by FotoOwl's "AI Gallery": guests scan a QR, take one selfie, and see **only the photos they appear in**.

Rules:
1. **Zero paid services.** No paid APIs, no cloud AI, no WhatsApp Business API, no paid licences. Everything must run locally with Docker Compose on one machine (Windows/Linux, CPU only; GPU optional).
2. Work **phase by phase** (section 12). At the end of each phase: build passes, tests pass, `docker compose up` works, then stop and give me a short summary + how to test it manually. Don't start the next phase until I say so.
3. Before writing code in a phase, show a short plan (files to create, decisions). Keep the plan short.
4. Explain backend concepts briefly in code comments and in `docs/ARCHITECTURE.md`. I'm a .NET developer who is learning how the AI part works, so explain the AI pipeline clearly.
5. Prefer simple, readable code over clever abstractions. Single admin user (me). Scale target: **1 wedding, ~5 functions, up to 15,000 photos, up to 500 guests**.
6. Face recognition will sometimes miss or mis-match. Design for that: thresholds are configurable, guests can say "not me" and "find more", and the admin can review matches.

## 1. Tech stack (fixed)

| Layer | Choice |
|---|---|
| Backend | **.NET 10 (LTS)**, ASP.NET Core Web API, C#, minimal APIs or controllers (controllers preferred) |
| ORM / DB | **EF Core + PostgreSQL 16 + pgvector** (`Npgsql.EntityFrameworkCore.PostgreSQL`, `Pgvector.EntityFrameworkCore`) |
| Background jobs | **Hangfire** with PostgreSQL storage (dashboard at `/hangfire`, admin only) |
| Images | **SixLabors.ImageSharp** (resize, EXIF orientation, watermark) |
| Storage | `IStorageService` abstraction. Default: local disk volume. Optional: MinIO (S3-compatible) |
| AI service | **Python 3.11 + FastAPI + InsightFace (`buffalo_l` model pack) + onnxruntime** in its own container: `face-service` |
| Frontend | **Angular (latest stable)**, standalone components, signals, Angular Material, PWA (`@angular/pwa`), responsive mobile-first |
| Real-time | **SignalR** (live gallery updates, processing progress) |
| Notifications | Email (SMTP, e.g. Gmail app password) + **Web Push** (VAPID, `WebPush` NuGet) + "Share on WhatsApp" `wa.me` links. No paid messaging |
| QR codes | **QRCoder** NuGet (PNG + printable poster PDF/PNG) |
| Video (phase 3) | **FFmpeg** in a container |
| Auth | Admin: ASP.NET Core Identity or simple JWT with one seeded admin. Guests: signed link token + optional 6-digit PIN → short-lived guest JWT |
| Infra | Docker Compose: `api`, `web` (nginx serving Angular + reverse proxy), `face-service`, `postgres`, (optional `minio`, `ffmpeg-worker`). Public access via **Cloudflare Tunnel** (free) documented in README |
| Tests | xUnit + FluentAssertions + Testcontainers (Postgres) for API; pytest for face-service; Jasmine/Karma or Jest for Angular |

## 2. Solution structure

```
shaadilens/
├─ docker-compose.yml
├─ .env.example
├─ docs/ SPEC.md, ARCHITECTURE.md, AI-PIPELINE.md, RUNBOOK.md
├─ backend/
│  ├─ ShaadiLens.sln
│  ├─ src/ShaadiLens.Api/             # controllers, SignalR hubs, auth, DI, Program.cs
│  ├─ src/ShaadiLens.Application/     # use cases, DTOs, interfaces (IStorageService, IFaceEngine, INotifier)
│  ├─ src/ShaadiLens.Domain/          # entities, enums, domain rules
│  ├─ src/ShaadiLens.Infrastructure/  # EF Core, migrations, storage, face-service HTTP client, Hangfire jobs, email, web push
│  └─ tests/ShaadiLens.Tests/
├─ face-service/                      # Python FastAPI
│  ├─ app/main.py, app/engine.py, app/quality.py
│  ├─ requirements.txt, Dockerfile, tests/
└─ frontend/shaadilens-web/           # Angular
   └─ src/app/ admin/, guest/, shared/, core/
```

## 3. Domain model (EF Core entities)

- **Event**: Id, Name ("Suraj & ___ Wedding"), Slug, Date, Venue, CoverImageKey, ThemeColor, TitleFont, WatermarkEnabled, WatermarkText/ImageKey, IsPublished, FaceSearchEnabled, ExpiresAt, CreatedAt.
- **Collection**: Id, EventId, Name (Haldi, Mehendi, Sangeet, Wedding, Reception), SortOrder, IsHidden.
- **Photo**: Id, EventId, CollectionId, OriginalFileName, ContentHash (SHA-256, for de-dup), StorageKeyOriginal, StorageKeyWeb (2048px long edge), StorageKeyThumb (400px), Width, Height, TakenAt (EXIF), CameraModel, Status (`Uploaded → Resized → FacesDetected → Matched | Failed`), FaceCount, BlurScore, IsHighlight, IsHidden, UploadedAt, Error.
- **Face**: Id, PhotoId, BBox (x,y,w,h normalized), Landmarks (5 points JSON), DetScore, Embedding `vector(512)` (L2-normalized), QualityScore, FaceSizePx, Yaw/Pitch, ClusterId (nullable).
- **Guest**: Id, EventId, Name, Phone, Email, SelfieKey, ConsentGivenAt, PushSubscriptionJson, CreatedAt, LastNotifiedAt, IsVip.
- **GuestReference**: Id, GuestId, Embedding `vector(512)`, Source (`Selfie | ConfirmedMatch`), Weight. (One guest can have several reference embeddings, which is how "find more" improves results.)
- **GuestMatch**: Id, GuestId, PhotoId, FaceId, Score, Status (`Auto | Probable | Confirmed | Rejected`), CreatedAt. Unique (GuestId, FaceId).
- **ShareLink**: Id, EventId, Type (`Guest | VipGuest | Full | Selection`), Token (random 32 chars, URL-safe), PinHash (nullable = no PIN), RequireRegistration, RegistrationFields (flags: Name, Phone, Email), AllowSingleDownload, AllowBulkDownload, SelectionLimit (Selection only), ExpiresAt, IsActive.
- **Selection**: Id, ShareLinkId, PhotoId, SelectedBy (name), Comment, CreatedAt. Plus **SelectionSubmission** (SubmittedAt, Locked).
- **AppSettings / FaceSettings** (per event): MatchThreshold (default 0.42), ProbableThreshold (default 0.34), MinFaceSizePx (default 40), MinDetScore (0.6), MaxSelfieYaw (30°).
- **AuditLog**: who/what/when for downloads, deletions, PIN failures.

Indexes: HNSW index on `Face.Embedding` and `GuestReference.Embedding` with `vector_cosine_ops`; index on (EventId, Status) for Photo.

## 4. AI pipeline (explain this in docs/AI-PIPELINE.md)

### 4.1 face-service API (Python FastAPI, internal network only)

- `POST /v1/analyze` (multipart image) → `{ width, height, faces: [{ bbox, kps, det_score, embedding[512], pose:{yaw,pitch,roll}, face_px, blur }] }`
  - Uses InsightFace `FaceAnalysis(name="buffalo_l", providers=["CPUExecutionProvider"])`, `prepare(ctx_id=-1, det_size=(640,640))`.
  - Embeddings must be L2-normalized (`face.normed_embedding`).
  - Downscale large images to ~1600px long edge before detection (speed); return bbox in original coordinates.
- `POST /v1/selfie` → same, plus validation: exactly one dominant face, face_px ≥ 80, |yaw| ≤ 30, det_score ≥ 0.7, blur (Laplacian variance) above threshold. Return `{ ok, reason, embedding }` with human-friendly reasons ("Move closer", "Only one face please", "Too dark/blurry").
- `GET /health`, `GET /v1/info` (model name, version).
- Model files are downloaded at image build time (or on first start into a volume) so it works offline at the venue.
- Pluggable: keep a `.NET IFaceEngine` interface so a pure-.NET engine (FaceAiSharp / ONNX Runtime) can replace the Python service later.

### 4.2 Photo processing (Hangfire jobs, per photo, idempotent, retry 3×)

1. **IngestJob**: compute SHA-256 (skip duplicates), read EXIF (date, orientation, camera), auto-orient, save original.
2. **ResizeJob**: create `web` (2048px, JPEG q82) and `thumb` (400px, q75), apply watermark to `web` if enabled. Push SignalR "photo added" to admin + Full-access viewers.
3. **FaceJob**: send resized image to face-service → save Face rows (skip faces smaller than MinFaceSizePx or det_score < MinDetScore).
4. **MatchNewFacesJob** (reverse search, gives "live" delivery): for each new face, pgvector query against all `GuestReference` embeddings of the event → create GuestMatch (score ≥ MatchThreshold → Auto; ≥ ProbableThreshold → Probable). Collect guests with new matches → debounce → **NotifyJob** (one notification per guest per 10 minutes max: "12 new photos of you").
- Concurrency: limit face jobs to N workers (config, default = CPU cores − 1). Show a progress bar (processed / total) on the admin dashboard via SignalR.

### 4.3 Selfie search (forward search)

1. Guest selfie → `/v1/selfie` → embedding → save Guest + GuestReference(Source=Selfie).
2. Query: `SELECT face, 1 - (embedding <=> @q) AS sim FROM faces WHERE event_id = @e ORDER BY embedding <=> @q LIMIT 2000`, keep sim ≥ ProbableThreshold.
3. Group by photo, take the best score per photo, store GuestMatch rows.
4. Guest sees **Auto** matches in "Your photos" and **Probable** matches in a separate "Might be you?" strip.

### 4.4 Handling misses ("AI tries and misses")

- **"Not me"** on a photo → GuestMatch.Status = Rejected (never shown again).
- **"Yes, that's me"** on a Probable photo → Confirmed, and that face's embedding is added as a GuestReference (Source=ConfirmedMatch).
- **"Find more photos of me"** → re-run the search using **all** the guest's reference embeddings (take the max similarity across references). This is query expansion and helps with side profiles, makeup, turbans/veils, and lighting.
- Allow guests to add a **second selfie** (different angle) to improve recall.
- Admin **Match Review** page: per guest, see matched faces with scores, approve/reject, adjust thresholds per event with a live preview ("at 0.40: 312 matches, at 0.45: 251").
- Admin **People view** (phase 2): cluster all faces (DBSCAN on cosine distance, eps≈0.5, in a Hangfire job or in face-service `/v1/cluster`) to show "Person 1, Person 2…" groups. Admin can name clusters and link a cluster to a guest.
- Group photos: a photo appears for every guest matched in it.
- Write a small **evaluation script** (`face-service/tools/eval.py`): given a folder of labelled photos, it reports precision/recall per threshold so I can tune the threshold on my own data.

## 5. Guest experience (Angular, mobile-first, no app install, no account)

Route: `/g/{token}`
1. **Landing**: cover image, couple names, date, "Find your photos" button.
2. **PIN screen** (if the link has a PIN): 6 digits, rate-limited (5 tries / 15 min per IP + token).
3. **Registration form** (fields per link config) + clear **consent checkbox**: "We use your selfie only to find your photos at this wedding. Delete anytime."
4. **Selfie capture**: `getUserMedia` front camera, oval face guide, on-device hints (optional: MediaPipe Face Detection in the browser for "face centred"); fall back to file upload. Show server validation reason and let them retry.
5. **"Matching your face…"** progress → **Your photos** grid (infinite scroll, lazy-loaded thumbs, lightbox with swipe, pinch-zoom), tabs per collection, "Might be you?" strip, "Not me" / "That's me" buttons.
6. Download single / Download all as ZIP (streamed ZIP, if allowed). "Share on WhatsApp" (`https://wa.me/?text=` + link).
7. "Notify me when new photos of me arrive" → Web Push opt-in (and email if provided). Guest session stored in a cookie/localStorage, so returning to the link skips selfie.
8. "Delete my data" → deletes Guest, selfie, references, matches.

VIP link: selfie only, no form. Full link: whole gallery by collection, with optional registration. Selection link: whole gallery + heart button, counter "37 / 40", comments per photo, "Submit selection" (locks it).

## 6. Admin experience (Angular, `/admin`)

- Login (single admin seeded from `.env`).
- **Dashboard**: events, photo count, guests registered, faces found, processing queue status.
- **Event editor**: name, date, venue, cover (desktop + mobile focal point), theme colour, font, watermark, collections (add/reorder/hide), expiry, publish toggle (nothing is visible to guests before publish).
- **Uploader**: drag-and-drop multiple files/folders, choose collection, **chunked/resumable upload** (tus via `tusdotnet`, or a simple chunk API), parallel 4, per-file progress, retry failed, duplicate detection. Max file size configurable (default 50 MB, JPEG/PNG/HEIC→convert).
- **Hot-folder uploader** (replacement for FotoOwl Beam): a small .NET console app/worker (`tools/HotFolderUploader`) that watches a folder (e.g., where the camera/Wi-Fi card or tethering software saves files) and uploads new JPEGs to a chosen collection with an API key.
- **Photos**: grid by collection, hide/delete/move, mark highlight, see face boxes overlay, processing status, re-run face detection.
- **Share**: 4 link types with settings (PIN on/off + regenerate, registration fields, downloads on/off, selection limit, expiry), copy link, **QR code PNG** and **printable A4/A5 poster** (couple names, "Scan · take a selfie · get your photos").
- **Guests**: list, match counts, last notified, resend notification, delete.
- **Match review & thresholds** (section 4.4), **People clusters** (phase 2).
- **Selections**: see picks + comments, copy file names as one comma-separated line (Lightroom filter), download picks as ZIP, export CSV.
- **Settings**: SMTP, VAPID keys (generate button), storage path, face-service URL, worker concurrency.

## 7. API (REST, versioned `/api/v1`, OpenAPI/Swagger in dev)

Admin (JWT, role Admin):
- `POST /auth/login`
- `GET/POST/PUT/DELETE /events`, `POST /events/{id}/publish`
- `GET/POST/PUT/DELETE /events/{id}/collections`
- `POST /events/{id}/uploads` (chunked/tus), `GET /events/{id}/photos?collection=&status=&page=`, `PATCH/DELETE /photos/{id}`, `POST /photos/{id}/reprocess`
- `GET/POST/PUT /events/{id}/share-links`, `GET /share-links/{id}/qr.png`, `GET /share-links/{id}/poster.png`
- `GET /events/{id}/guests`, `GET /guests/{id}/matches`, `PATCH /matches/{id}`, `DELETE /guests/{id}`
- `PUT /events/{id}/face-settings`, `GET /events/{id}/face-settings/preview?threshold=`
- `GET /events/{id}/selections`, `GET /events/{id}/selections/export?format=names|csv|zip`
- `GET /events/{id}/stats`

Guest (link token → guest JWT, scoped to one event + one link type):
- `GET /public/links/{token}` (event branding, link type, needs PIN?, registration fields)
- `POST /public/links/{token}/verify-pin`
- `POST /public/links/{token}/register` (form + selfie multipart) → guest JWT
- `GET /public/me/photos?collection=&page=` (Auto + Confirmed), `GET /public/me/probable`
- `POST /public/me/matches/{photoId}/confirm|reject`, `POST /public/me/find-more`, `POST /public/me/selfie` (extra selfie)
- `GET /public/gallery?collection=&page=` (Full/Selection links only)
- `GET /public/photos/{id}/download`, `GET /public/me/download-all.zip`
- `POST/DELETE /public/selections/{photoId}`, `PUT /public/selections/{photoId}/comment`, `POST /public/selections/submit`
- `POST /public/me/push-subscription`, `DELETE /public/me`

Image serving: `GET /media/{photoId}/{thumb|web|original}` with **short-lived signed URLs** (HMAC, 1 h), and an authorization check that the guest is allowed to see that photo (Guest links → only matched photos). Set long `Cache-Control` on immutable variants.

SignalR hub `/hubs/events`: `PhotoProcessed`, `ProcessingProgress`, `NewMatchesForYou` (guest group).

## 8. Security and privacy

- HTTPS via Cloudflare Tunnel; HSTS; secure cookies; CORS locked to the web origin.
- PINs hashed (PBKDF2/BCrypt), link tokens cryptographically random, rate limiting (`AddRateLimiter`) on PIN, register, selfie endpoints.
- Guest JWT scoped to `eventId + linkId + guestId`, 30-day expiry.
- Every media request authorized; originals only when downloads are allowed.
- Consent stored with timestamp; "delete my data" works; selfies and embeddings auto-purged when the event expires (Hangfire recurring job).
- Never log embeddings, selfies, phones or emails. Secrets only via `.env`.
- Data-protection note in README: face data is biometric; this is for a private family event.

## 9. Performance targets (CPU-only laptop/PC)

- Upload → thumbnail visible: < 5 s per photo.
- Face analysis: ~0.3–1 s per photo on CPU at det_size 640; 10,000 photos processed overnight is acceptable. Log throughput.
- Selfie search over 50,000 faces: < 1 s (pgvector HNSW).
- Gallery: lazy loading, `srcset` thumb/web, virtual scroll for big grids.

## 10. Configuration (.env)

`POSTGRES_*`, `ADMIN_EMAIL`, `ADMIN_PASSWORD`, `JWT_SECRET`, `MEDIA_SIGNING_KEY`, `STORAGE_ROOT=/data/photos`, `FACE_SERVICE_URL=http://face-service:8000`, `FACE_WORKERS`, `SMTP_HOST/PORT/USER/PASS/FROM`, `VAPID_PUBLIC/PRIVATE/SUBJECT`, `PUBLIC_BASE_URL` (used in QR codes and emails).

## 11. Quality bar

- Clean architecture, async everywhere, cancellation tokens, `ProblemDetails` errors, FluentValidation on inputs, Serilog structured logs, health checks (`/health` for api + face-service + db).
- EF migrations committed; seed a demo event with sample collections.
- Unit tests for: matching logic (thresholds, Rejected never returned, group photos), PIN/rate-limit, share-link authorization (Guest link can't fetch unmatched photo), selection limit enforcement.
- Face-service tests with 3 sample images (one face, no face, group).
- README: prerequisites, `docker compose up`, first login, creating the wedding, printing QR, exposing via Cloudflare Tunnel, backup/restore of Postgres + photos volume, tuning thresholds.

## 12. Build phases

**Phase 0 — Scaffold (½ day)**: repo layout, Docker Compose (postgres+pgvector, api, web, face-service), health checks, Swagger, Angular shell with routing (admin + guest), CI-ready build scripts. Face-service `/v1/analyze` working on a sample image.

**Phase 1 — MVP gallery + face search (core)**: admin login, events, collections, chunked upload, resize + EXIF, Hangfire pipeline, face detection + embeddings stored in pgvector, Guest share link with PIN + registration + selfie capture, selfie search, "Your photos" gallery with lightbox and downloads, Full Access gallery, QR code + poster, signed media URLs. **Acceptance**: upload 200 real photos, register with my selfie, see my photos within a minute.

**Phase 2 — Live + feedback + couple features**: reverse matching on new uploads, Web Push + email notifications, SignalR live updates, Not me / That's me / Find more / second selfie, admin Match Review + threshold preview, People clusters, Photo Selection link (hearts, limit, comments, submit, export names/ZIP/CSV), VIP link, watermark, theme/branding, expiry + purge job, TV slideshow route `/tv/{token}` (fullscreen, auto-advance, by collection, keyboard/remote friendly), hot-folder uploader tool.

**Phase 3 — Extras (optional)**: simple AI culling report (blur score via Laplacian variance, eyes-closed via eye landmarks / eye-aspect ratio, near-duplicate grouping via perceptual hash) shown as filters in admin; per-guest **reel** generator with FFmpeg (top N photos by face size + quality, Ken Burns zoom, crossfade, royalty-free music from a local folder, small watermark, 9:16 MP4) queued as a Hangfire job; highlights/stories strip.

**Not in scope**: payments, gallery selling, CRM/lead agent, multi-tenant studios, native mobile apps, paid WhatsApp API.

## 13. Start now

Start with **Phase 0**. First show me the plan and the docker-compose.yml, then create the files. Explain any command I need to run on my machine (Windows + Docker Desktop).
