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

## 4. Data flow *(Phase 1)*

Upload → IngestJob (hash, EXIF, orient) → ResizeJob (web + thumb) → FaceJob (face-service → Face rows with
embeddings) → MatchNewFacesJob (pgvector search against guest selfies) → NotifyJob. See `AI-PIPELINE.md`.

## 5. Configuration

All secrets via `.env` → environment variables → ASP.NET Core configuration
(`FaceService__BaseUrl` ⇒ `FaceService:BaseUrl`). `appsettings.json` holds only local-dev defaults.
