# Runbook

> Day-to-day operations: start/stop, troubleshooting, public access, backups.

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
A faster desktop CPU is typically 2–3× quicker. Batched embeddings (done in Phase 1) cut a 6-face photo to
~2.2 s. Further ideas: `FACE_DET_SIZE=480` for close-up-heavy collections, GPU provider.

## Opening it to phones — Cloudflare Tunnel (free)

Phones need **HTTPS** for the live selfie camera (on plain `http://192.168.x.x` they fall back to the phone's
camera app, which still works). Cloudflare Tunnel gives you a public HTTPS address without opening router ports.

**Quick test (no account, random address, changes each run):**
```powershell
winget install --id Cloudflare.cloudflared
cloudflared tunnel --url http://localhost:8080
# prints https://something-random.trycloudflare.com
```
Put that address in `.env` as `PUBLIC_BASE_URL=https://something-random.trycloudflare.com`, then
`docker compose up -d api` so QR codes and posters use it. Re-print posters if the address changes.

**For the wedding (fixed address, needs a free Cloudflare account + a domain on Cloudflare):**
```powershell
cloudflared tunnel login
cloudflared tunnel create shaadilens
cloudflared tunnel route dns shaadilens photos.yourdomain.com
cloudflared tunnel run --url http://localhost:8080 shaadilens
```
Set `PUBLIC_BASE_URL=https://photos.yourdomain.com` and `ASPNETCORE_ENVIRONMENT=Production` (turns Swagger off).
nginx passes Cloudflare's `X-Forwarded-Proto: https` through, so the admin cookie is marked Secure.

## Backup / restore

Everything lives in two Docker volumes: `shaadilens_pgdata` (database) and `shaadilens_photos` (files).

```powershell
mkdir backup
# database (consistent dump while running)
docker compose exec -T postgres pg_dump -U shaadilens -Fc shaadilens > backup\db.dump
# photos + selfies
docker run --rm -v shaadilens_photos:/data -v ${PWD}\backup:/backup alpine tar czf /backup/photos.tgz -C /data .
```
Restore on a fresh machine:
```powershell
docker compose up -d postgres
docker compose exec -T postgres pg_restore -U shaadilens -d shaadilens --clean --if-exists < backup\db.dump
docker run --rm -v shaadilens_photos:/data -v ${PWD}\backup:/backup alpine tar xzf /backup/photos.tgz -C /data
docker compose up -d
```

## Tuning thresholds
Overview tab → Face matching → **Preview thresholds** shows how many matches each value gives (histogram of all
guest × photo similarities; real matches pile up on the right). Raise **Match** if strangers show up in
"Your photos"; lower **Probable** if people miss photos. **Save**, then **Apply to all guests** (background job,
usually < 1 min). New uploads always use the saved values. Per guest: Guests → ✔ icon = Match Review
(approve/reject faces; approving adds the face as a reference for "Find more").
