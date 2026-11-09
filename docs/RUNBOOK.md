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
| Admin progress bar doesn't move live | Live updates use WebSockets on `/hubs`; the page falls back to polling every 3 s. Check `docker compose logs web api` |
| "Notify me" missing for guests | Web Push needs VAPID keys (Settings) or SMTP + the guest's email; phones also need HTTPS |
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


## Notifications (optional)

**Web Push** (free; works on Android/desktop; iPhone only after "Add to Home Screen", iOS 16.4+):
1. Admin → ⋮ → **Settings** → **Generate VAPID keys** → copy the two lines into `.env`.
2. `docker compose up -d api`. Guests now see **Notify me** in their gallery.
3. Phones need HTTPS for this → use the Cloudflare Tunnel address (above).

**Email** (e.g. Gmail): in `.env` set `SMTP_HOST=smtp.gmail.com`, `SMTP_PORT=587`, `SMTP_USER=you@gmail.com`,
`SMTP_PASS=<App Password>` (Google Account → Security → 2-Step Verification → App passwords), `SMTP_FROM=you@gmail.com`.
Restart the api, then Settings → **Send test email**. Guests who gave an email can tick "Also email me".

Guests get at most one message per 10 minutes ("12 new photos of you!"). Guests → **send** icon resends now.

## Album selection (for the couple)
1. Share → **New link** → type **Selection link**, set "How many photos can they pick?" (e.g. 40).
2. Send the link to the couple. They tap ❤ on photos, add notes, then **Submit selection** (locks it).
3. **Selections** tab: **Copy file names** → Lightroom: Library → Filter (`\`) → Text → Filename → Contains → paste.
   Or download **CSV** / **ZIP** of the originals. **Unlock** lets them change their picks again.

## Watermark, fonts, closing the gallery
- Overview → Details: **Watermark** (text) and **Title font**. After changing the watermark, answer "yes" to apply it
  to existing photos (background job). Browsers that already opened a photo may show the old one for up to an hour.
- **Gallery closes on**: after that day guest links stop working, and within an hour all selfies and face data are
  deleted automatically. Photos and album picks stay. Extend the date *before* it passes if you need more time.

## TV slideshow (venue screen)
Share → create a **Full gallery link** with "Ask guests to fill a form" off → **TV slideshow**. Open it on the TV/laptop
browser, click once for fullscreen. `?interval=8` changes the seconds per photo, `?collection=Sangeet` limits it to
one collection. Keys: ← → · Space pause · F fullscreen · C next collection. New photos appear by themselves.

## Hot-folder uploader (upload while you shoot)
Uploads every new photo that lands in a folder (camera Wi-Fi card, tethering software, card reader) into one collection.
1. Put a random key of 24+ characters in `.env`: `UPLOADER_API_KEY=…`, then `docker compose up -d api`.
2. On the laptop with the photos (needs the .NET 10 SDK, or run it in Docker as below):
   ```powershell
   dotnet run --project backend/tools/HotFolderUploader -- --folder "D:\Camera" --event <event-slug> --collection Wedding --key <UPLOADER_API_KEY>
   ```
   Without a local SDK:
   ```powershell
   docker run --rm -it -v ${PWD}\backend:/src -v D:\Camera:/photos mcr.microsoft.com/dotnet/sdk:10.0 `
     dotnet run --project /src/tools/HotFolderUploader -- --folder /photos --event <slug> --collection Wedding `
     --api http://host.docker.internal:8080 --key <UPLOADER_API_KEY>
   ```
It waits until a file is completely written, uploads 2 at a time, retries on network errors, remembers what it sent
(restarts don't re-upload), and the server skips exact duplicates anyway. `--once` uploads what's there and exits.
The key can only upload — it can't see guests or change anything else.
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

## Culling report (which photos to skip)
Photos tab → chips **Blurry · Eyes closed · Duplicates**. Duplicates shows one row per burst with its keeper
outlined; **Keep this one** changes the keeper, **Keep best, hide the rest** hides the others from guests (undo:
Hidden chip → open → Show to guests). Events uploaded before this feature: click **Analyze them** once.
Thresholds (rarely needed) in `.env`, e.g. `Culling__BlurryFaceBelow=60`, `Culling__ClosedEyesBelow=0.17`,
`Culling__DuplicateMaxBits=10`, `Culling__BurstWindowSeconds=60`, then `docker compose up -d api` and re-run
face detection / Analyze. See docs/AI-PIPELINE.md §4g for what they mean.

## Highlight reels (guest videos)
Guests with a selfie see **Your highlight reel → Make my reel** once they have ≥ 3 photos. The `reel-worker`
container renders it (Settings → Highlight reels shows whether it runs). Per event: Overview → Highlight reels
(on/off, photos per reel). Music: put royalty-free tracks in `./music` (see music/README.md), no restart needed.
Speed (i5-8250U, `REEL_CPUS=2`): 12 photos ≈ 2 min, 30 s video ≈ 12 MB. More cores: `REEL_CPUS=4` in `.env`,
then `docker compose up -d reel-worker`. A reel stuck at "Making your reel…": `docker compose ps reel-worker`,
`docker compose logs reel-worker`. Limits: one reel at a time, 3 per guest per 24 h (`Reels__MaxPerDay`).

## Highlights strip ("stories")
Photos → **Highlights** chip → **Suggest highlights** → untick what you don't like → **Mark N as highlights**
(or ★ in a single photo). Guests see one bubble per collection at the top of their gallery. Overview →
Highlights strip: turn off "Show all highlights to every guest" if guests should only see highlights they're in.
