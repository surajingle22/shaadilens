# Deploying ShaadiLens to a server

> Status: **work in progress**. This guide grows step by step: server setup (here) → ARM64 images + CI →
> HTTPS (Caddy) → push-to-deploy + rollback → backups → ops. Sections for later steps are added as they land.

Target: one Ubuntu 24.04 VM — tested on an Oracle Cloud **Always Free Ampere A1** (ARM64, 2 OCPU / 12 GB,
150 GB boot volume, Mumbai). Any Ubuntu 24.04 VPS (x86_64 or ARM64) works the same way.

## How it fits together

```
 GitHub (push to main) ──► GitHub Actions: tests → build images → push to GHCR ──ssh──► VM: deploy.sh <sha>
                                                                                          │
 phone ──HTTPS──► VM :443  Caddy (Let's Encrypt) ──► web (nginx + Angular) ──► api ──► postgres / face-service
```

The VM never builds anything. It only pulls ready images from GitHub Container Registry (GHCR) and runs them
with Docker Compose.

## 1. Create the server (Oracle Cloud)

1. Sign up at https://signup.cloud.oracle.com. The **home region** cannot be changed later, and Always Free
   ARM VMs only run there. Upgrade to **Pay As You Go** (Billing → Upgrade and Manage Payment): Always Free
   resources stay free, and capacity for A1 VMs is easier to get. Expect a temporary card hold that is reversed.
2. Add a budget alert: Billing → Budgets → Create Budget → monthly amount 100 (₹), alert on *actual* spend at 1 %.
3. Make an SSH key on your laptop (PowerShell):
   ```powershell
   New-Item -ItemType Directory -Force "$env:USERPROFILE\.ssh"
   ssh-keygen -t ed25519 -C "shaadilens-oci" -f "$env:USERPROFILE\.ssh\shaadilens_oci"
   Get-Content "$env:USERPROFILE\.ssh\shaadilens_oci.pub"     # the PUBLIC key — paste this into Oracle
   ```
4. Compute → Instances → **Create instance**: shape **VM.Standard.A1.Flex** (2 OCPU, 12 GB), image
   **Canonical Ubuntu 24.04**, new VCN + public subnet, paste the public key, custom boot volume **150 GB**.
   - **"Out of capacity"**: retry later (off-peak hours IST), or create it with 1 OCPU / 6 GB and resize later
     (Instance → Edit → Shape). Regions with one availability domain (Mumbai) can only retry.
   - No public IP? Instance → Attached VNICs → the VNIC → IPv4 addresses → ⋮ Edit → **Ephemeral public IP**.
5. Open the cloud firewall: instance → subnet → **Security** → Default Security List → **Add Ingress Rules**:
   source `0.0.0.0/0`, TCP, destination port `80`; again for `443`.
6. DuckDNS: sign in at https://www.duckdns.org, add a subdomain (e.g. `shaadilens`), set *current ip* to the
   VM's public IP. Keep the token secret — it goes only into the server's `.env`.

> **Two firewalls.** Oracle filters traffic twice: the *security list* (cloud firewall, step 5) and *iptables*
> inside the VM. Oracle's Ubuntu image rejects everything except SSH in iptables; `bootstrap.sh` fixes that.

## 2. Bootstrap the server (once)

```powershell
ssh -i $env:USERPROFILE\.ssh\shaadilens_oci ubuntu@<VM_IP>
```
On the VM:
```bash
curl -fsSL https://raw.githubusercontent.com/surajingle22/shaadilens/main/deploy/bootstrap.sh -o bootstrap.sh
sudo bash bootstrap.sh
sudo -u deploy nano /opt/shaadilens/.env      # fill every CHANGE_ME line, Ctrl+O Enter Ctrl+X
```

`bootstrap.sh` is idempotent (running it again only fixes what is missing). It installs Docker + the compose
plugin with log rotation, opens ports 80/443 in iptables (persisted in `/etc/iptables/rules.v4`), adds a 4 GB
swap file, turns on unattended security upgrades (no automatic reboot), creates the `deploy` user, clones the
repo to `/opt/shaadilens/app` and creates `/opt/shaadilens/.env` (chmod 600) with random secrets. It also adds
a cron job that keeps DuckDNS pointing at the VM.

| Path | What |
|---|---|
| `/opt/shaadilens/.env` | production settings + secrets. Edit with `sudo -u deploy nano …`. Never copy it anywhere public. |
| `/opt/shaadilens/app` | git checkout; only compose files, Caddyfile and scripts are used |
| `/opt/shaadilens/music` | royalty-free tracks for reels (`scp` them here) |
| `/opt/shaadilens/backups` | database dumps and photo snapshots |
| `/opt/shaadilens/logs` | cron job logs (`duckdns.log`, …) |

Check DuckDNS after editing `.env` (wait up to 5 minutes, or run it now):
```bash
sudo -u deploy /opt/shaadilens/app/deploy/duckdns-update.sh; cat /opt/shaadilens/logs/duckdns.log
```

## 3. Container images (built by CI)

The server never builds anything. On every push to `main`, GitHub Actions (`.github/workflows/ci.yml`):

1. runs all tests;
2. **`images`** — builds each image twice, natively: **amd64** on a normal runner and **arm64** on GitHub's ARM
   runner (the Oracle VM is ARM64), and pushes both to GitHub Container Registry (GHCR) *by digest* (no tag yet);
3. **`verify-arm64`** — on a real ARM machine: face-service unit tests + the real face model on a sample photo
   (`/v1/analyze`, `/v1/selfie`), an ffmpeg H.264 test encode in reel-worker, and the whole stack via
   `.github/ci/stack-smoke.sh` (`/health` = Healthy, admin login, chunked upload, faces detected, thumbnail served);
4. **`manifests`** — only if that passed: tags each image `:<full commit sha>` and `:latest`. One tag covers both
   CPU types ("multi-arch image"); Docker on the server picks the arm64 build automatically.

On pull requests, `verify-arm64` builds the arm64 images on the ARM runner and runs the same checks, without pushing.

| Image | Built from |
|---|---|
| `ghcr.io/surajingle22/shaadilens-api` | `backend`, target `runtime` |
| `ghcr.io/surajingle22/shaadilens-reel-worker` | `backend`, target `reel` (api + ffmpeg) |
| `ghcr.io/surajingle22/shaadilens-face-service` | `face-service` (includes the InsightFace model) |
| `ghcr.io/surajingle22/shaadilens-web` | `frontend/shaadilens-web` (Angular + nginx) |

The packages must stay **private**: the face-service image contains the InsightFace `buffalo_l` model, which is
licensed for non-commercial use only and must not be redistributed. Check: GitHub → your profile → **Packages** →
each package → **Package settings** → *Danger Zone* → visibility **Private**.

Check that a tag has both CPU types (needs `docker login ghcr.io` with a token that can read packages):
```bash
docker buildx imagetools inspect ghcr.io/surajingle22/shaadilens-api:latest | grep Platform
```

Run the stack smoke test locally from images you built yourself (own compose project + port, cleans up after):
```bash
FACE_IMAGE=shaadilens/face-service:dev API_IMAGE=shaadilens/api:dev REEL_IMAGE=shaadilens/reel-worker:dev \
WEB_IMAGE=shaadilens/web:dev .github/ci/stack-smoke.sh      # needs bash, curl, jq, openssl
```

Later, the GitHub Actions key is added with:
```bash
sudo bash /opt/shaadilens/app/deploy/bootstrap.sh --deploy-key "ssh-ed25519 AAAA… github-actions-deploy"
```

## 4. HTTPS with Caddy (production compose)

**Reverse proxy, briefly:** one public entry point that forwards requests to services on Docker's private network.
Caddy is that entry point. On first start it gets a free **Let's Encrypt** certificate for `DOMAIN` (Let's Encrypt
checks you control the domain by calling `http://DOMAIN/.well-known/…`, so port 80 must be open and DuckDNS must
point at the VM), renews it by itself every ~60 days and redirects `http://` to `https://`.

```
 phone ─HTTPS─► caddy :80/:443 ──► web (nginx: Angular + /api /media /hubs) ──► api ──► postgres / face-service
                (only public ports)     (no published ports from here on)            reel-worker
```

Files: `docker-compose.prod.yml` (override of `docker-compose.yml`) and `deploy/Caddyfile`.

| | Dev (`docker compose up`) | Production (`-f docker-compose.yml -f docker-compose.prod.yml`) |
|---|---|---|
| Images | built locally | pulled from GHCR, tag `IMAGE_TAG` (commit SHA) |
| Public ports | web :8080 (+ debug ports from the dev override) | **only Caddy 80/443** |
| HTTPS | none (or Cloudflare Tunnel) | Caddy + Let's Encrypt, HSTS |
| `/hangfire` | admin login | **404 from the internet**; use the SSH tunnel below |
| Swagger | on (`Development`) | off (`ASPNETCORE_ENVIRONMENT=Production` in `.env`) |
| Logs | Docker default | 10 MB × 3 per container |
| Music for reels | `./music` | `/opt/shaadilens/music` |

Certificates live in the `shaadilens_caddy_data` volume. Never delete it casually: Let's Encrypt allows only a
few new certificates per domain per week.

### First deploy by hand (before push-to-deploy exists)

The GHCR packages are private, so the VM needs a token to pull. Create a **classic** token with only
`read:packages`, 7-day expiry (GitHub → Settings → Developer settings → Personal access tokens → Tokens (classic)),
then on the VM as `deploy`:
```bash
sudo -iu deploy
cd /opt/shaadilens/app && git pull --ff-only
docker login ghcr.io -u surajingle22          # paste the token when asked (it is not shown)
export IMAGE_TAG=latest
dc() { docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file /opt/shaadilens/.env "$@"; }
dc pull && dc up -d
dc ps                                         # wait until everything is "healthy" (face-service ~1 min)
docker logout ghcr.io                         # don't leave the token on the server
curl -sS https://<your-domain>/health         # {"status":"Healthy",…}
```
First start: Caddy needs ~10–30 s for the certificate (`dc logs caddy`). The api applies database migrations
on start by itself (`DatabaseInitializer`), so there is no separate migration step.

### Hangfire dashboard (SSH tunnel)

`/hangfire` is blocked at Caddy. To open it, forward a local port to the `web` container through SSH (PowerShell):
```powershell
ssh -i $env:USERPROFILE\.ssh\shaadilens_oci -L 8081:localhost:8081 ubuntu@<VM_IP> `
  "sudo docker run --rm -p 127.0.0.1:8081:8081 --network shaadilens_default alpine/socat TCP-LISTEN:8081,fork TCP:web:80"
```
Then log in at http://localhost:8081/admin and open http://localhost:8081/hangfire. Ctrl+C closes it.

### Troubleshooting HTTPS

| Symptom | Check |
|---|---|
| Browser: connection refused / timeout | Oracle security list has TCP 80 + 443; `sudo iptables -L INPUT -n --line-numbers` shows ACCEPT 80/443 above REJECT; `dc ps caddy` |
| Caddy log: `challenge failed` / `NXDOMAIN` | DuckDNS points at the VM: `nslookup <domain>` = VM IP; port 80 reachable from outside |
| Caddy log: `too many certificates` / `rateLimited` | Let's Encrypt rate limit — wait (see the time in the log); don't delete `caddy_data` |
| Selfie camera doesn't open on the phone | page must be `https://`; check the padlock, and `PUBLIC_BASE_URL=https://${DOMAIN}` in `.env` |
| Live updates fall back to polling | WebSockets through Caddy work by default; check `dc logs web api` for `/hubs` errors |
