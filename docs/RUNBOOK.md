# Runbook

> Filled in progressively. Phase 0 covers start/stop and troubleshooting.

## Start / stop

```powershell
docker compose up -d --build     # start (dev: also exposes api/face/postgres on localhost)
docker compose ps                # all should be "healthy"
docker compose logs -f api       # follow logs
docker compose down              # stop (data volumes are kept)
docker compose down -v           # stop AND DELETE all data (postgres + photos). Careful!
```

Production-style (no debug ports): `docker compose -f docker-compose.yml up -d`.

## Troubleshooting

| Symptom | Check |
|---|---|
| `/health` shows `face-service: Unhealthy` right after start | Normal for ~30–60 s while models load. `docker compose logs face-service` |
| face-service image build fails on model download | Network hiccup; re-run `docker compose build face-service` (curl retries + resumes) |
| Port 8080 already in use | Set `WEB_PORT=8081` in `.env` |
| Postgres password changed in `.env` but login fails | The password is set only when the volume is first created. `docker compose down -v` (deletes data) or change it inside Postgres |

## Measured performance

| Machine | Detection | Per face (embedding + pose) | 1280×886 photo, 6 faces |
|---|---|---|---|
| i5-8250U laptop (4C/8T), Docker Desktop, 2026-11-07 | ~410 ms | ~380 ms | ~2.9–3.4 s |

Rule of thumb: `time ≈ 0.4 s + 0.38 s × faces`. 15,000 photos ≈ 6–10 h on this laptop (overnight).
A faster desktop CPU is typically 2–3× quicker. Ideas if needed: batch embeddings per photo (Phase 1),
`FACE_DET_SIZE=480` for close-up-heavy collections, GPU provider.

## Backup / restore *(Phase 1)*
## Cloudflare Tunnel *(Phase 1)*
## Tuning thresholds *(Phase 2)*
