# ShaadiLens

Self-hosted, free, AI wedding photo sharing. Guests scan a QR code, take one selfie, and see only the photos they're in.
Runs entirely on one machine with Docker Compose — no paid APIs, no cloud AI.

> **Privacy note:** face embeddings are biometric data. This app is meant for a private family event:
> guests give explicit consent, can delete their data at any time, and data is purged when the event expires.

**Status:** Phase 3 in progress — 3a culling report done (blurry / eyes closed / near-duplicates). Phase 2 complete — live matching, feedback, notifications, couple features, people, TV, hot folder.
See [docs/SPEC.md](docs/SPEC.md) §12.

## What it does
- **Guests** scan a QR, take a selfie, see only their photos; "That's me / Not me", "Find more", a second selfie,
  "Notify me" (Web Push / email), download single or ZIP, delete their data. New photos arrive live.
- **Links**: Guest (selfie), VIP (selfie, no form), Full gallery, Selection (the couple hearts album picks).
- **Admin**: chunked uploads (+ a hot-folder uploader for the camera), live processing, Match Review,
  threshold preview, People clusters, Selections export (Lightroom/CSV/ZIP), watermark, branding, expiry purge,
  QR posters, TV slideshow (`/tv/{token}`).

## Stack
.NET 10 · EF Core + PostgreSQL 16 + pgvector · Hangfire · Magick.NET · Python FastAPI + InsightFace (`buffalo_l`, CPU) · Angular 22 + Material · nginx · Docker Compose

## Prerequisites
- Docker Desktop (WSL 2 backend on Windows), ~4 GB free disk
- Optional for local dev outside Docker: .NET 10 SDK, Node 24 LTS (Angular 22 needs Node ≥ 22.22 or ≥ 24.15)

## Quick start

```powershell
copy .env.example .env      # then edit POSTGRES_PASSWORD (and later the other secrets)
docker compose up --build   # first build: ~5–10 min (downloads the ~280 MB face model once)
```

| URL | What |
|---|---|
| http://localhost:8080 | Angular app (admin dashboard shows system status) |
| http://localhost:8080/g/demo | Guest landing page |
| http://localhost:5080/swagger | API Swagger UI (dev only) |
| http://localhost:5080/health | API health: Postgres + face-service |
| http://localhost:8000/docs | face-service FastAPI docs (dev only) |

Try face detection on your own photo:
```powershell
curl.exe -F "file=@C:\path\to\photo.jpg" http://localhost:8000/v1/analyze
```

## Build & test
```powershell
./scripts/build.ps1               # everything, via Docker toolchains
./scripts/build.ps1 -Only backend # backend | frontend | face
```

## Local dev without Docker for api/web
```powershell
docker compose up -d postgres face-service          # dependencies only
cd backend;  dotnet run --project src/ShaadiLens.Api   # http://localhost:5090/swagger
cd frontend/shaadilens-web; npm install; npm start     # http://localhost:4200 (proxies /api to :5080)
```
(`proxy.conf.json` targets the Docker API on 5080; change it to 5090 if you run the API with `dotnet run`.)

## Docs
- [Architecture](docs/ARCHITECTURE.md)
- [How the face matching works](docs/AI-PIPELINE.md)
- [Runbook](docs/RUNBOOK.md)
- Day-to-day: notifications, album selection, watermark, TV slideshow, hot-folder uploader → [Runbook](docs/RUNBOOK.md)
