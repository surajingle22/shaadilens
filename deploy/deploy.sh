#!/usr/bin/env bash
# ShaadiLens — deploy one version (a commit SHA) on the server. Runs as the `deploy` user.
#
#   ./deploy/deploy.sh <40-char commit sha>        normally started by GitHub Actions (job "deploy")
#   ./deploy/rollback.sh [sha]                     back to the previous version (wraps this script)
#
# Steps: lock → config files at <sha> → database dump (pre-deploy) → pull images :<sha> → up -d →
# wait until api, web, caddy are healthy AND https://$DOMAIN/health/live answers → record the version.
# If anything fails after the switch, it rolls back to the version that was running before, then exits 1.
# Images must be pullable: GitHub Actions logs in to ghcr.io for the duration of the job.
# Prints no secrets.
set -euo pipefail

APP_ROOT="${APP_ROOT:-/opt/shaadilens}"
APP_DIR="$APP_ROOT/app"
ENV_FILE="$APP_ROOT/.env"
STATE="$APP_ROOT/state"
DB_BACKUPS="$APP_ROOT/backups/db"
LOGS="$APP_ROOT/logs"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-360}"   # seconds (first start: Caddy fetches a certificate, face model loads)
KEEP_DUMPS=5
KEEP_VERSIONS=3                          # current + 2 previous image sets stay on disk for fast rollbacks

log()  { printf '%s %s\n' "$(date '+%H:%M:%S')" "$*"; }
fail() { log "ERROR: $*"; return 1; }

[[ $# -eq 1 && "$1" =~ ^[0-9a-f]{40}$ ]] || { echo "usage: $0 <40-character commit sha>" >&2; exit 2; }
TARGET="$1"
[[ -r "$ENV_FILE" ]] || { echo "cannot read $ENV_FILE (run as the deploy user)" >&2; exit 1; }
mkdir -p "$STATE" "$DB_BACKUPS" "$LOGS"

# One deploy at a time: a second one waits up to 15 minutes, then gives up.
exec 9>"$STATE/deploy.lock"
flock -w 900 9 || { echo "another deploy is still running" >&2; exit 1; }

# Read one KEY=value from .env without executing it (values may contain $ etc.).
env_get() { grep -E "^$1=" "$ENV_FILE" | tail -n1 | cut -d= -f2- | tr -d '\r' | sed -e 's/^"//' -e 's/"$//'; }
DOMAIN="$(env_get DOMAIN)"
[[ -n "$DOMAIN" && "$DOMAIN" != *CHANGE_ME* ]] || { echo "DOMAIN is not set in $ENV_FILE" >&2; exit 1; }
if grep -qE '^[A-Z_]+=.*CHANGE_ME' "$ENV_FILE"; then
  echo "These .env lines still say CHANGE_ME: $(grep -E '^[A-Z_]+=.*CHANGE_ME' "$ENV_FILE" | cut -d= -f1 | paste -sd' ')" >&2
  exit 1
fi

compose() {
  docker compose --project-directory "$APP_DIR" -f "$APP_DIR/docker-compose.yml" -f "$APP_DIR/docker-compose.prod.yml" \
    --env-file "$ENV_FILE" "$@"
}

checkout() {   # config files (compose, Caddyfile, scripts) at <sha>; the images carry the code
  git -C "$APP_DIR" fetch -q origin
  git -C "$APP_DIR" -c advice.detachedHead=false checkout -q --detach "$1"
}

predeploy_dump() {   # forward-only migrations: keep a dump of the database as it was before this deploy
  local old="$1" file
  if [[ -z "$(compose ps -q --status running postgres 2>/dev/null)" ]]; then
    log "database dump: skipped (postgres not running yet)"
    return 0
  fi
  file="$DB_BACKUPS/predeploy-$(date +%Y%m%d-%H%M%S)-${old:0:8}.dump"
  # shellcheck disable=SC2016  # $POSTGRES_* expand inside the container
  if compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' > "$file.tmp"; then
    mv "$file.tmp" "$file"
    log "database dump: $(basename "$file") ($(du -h "$file" | cut -f1))"
  else
    rm -f "$file.tmp"
    fail "pre-deploy database dump failed — not deploying"
  fi
  # keep the newest $KEEP_DUMPS pre-deploy dumps
  find "$DB_BACKUPS" -maxdepth 1 -name 'predeploy-*.dump' -printf '%T@ %p\n' | sort -rn | tail -n +$((KEEP_DUMPS + 1)) \
    | cut -d' ' -f2- | xargs -r rm -f
}

wait_healthy() {   # api, web, caddy healthy + https://$DOMAIN/health/live = 200 (through Caddy, real certificate)
  local deadline=$(( $(date +%s) + HEALTH_TIMEOUT )) svc state code all
  while :; do
    all=1
    for svc in api web caddy; do
      state="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' \
        "$(compose ps -q "$svc" 2>/dev/null)" 2>/dev/null || echo missing)"
      [[ "$state" == healthy ]] || all=0
    done
    # --resolve: test this server's own Caddy without depending on the cloud's NAT hairpin
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 --resolve "$DOMAIN:443:127.0.0.1" \
      "https://$DOMAIN/health/live" 2>/dev/null || true)"
    if [[ $all -eq 1 && "$code" == 200 ]]; then return 0; fi
    if (( $(date +%s) > deadline )); then
      log "not healthy after ${HEALTH_TIMEOUT}s (https /health/live: HTTP ${code:-none})"
      compose ps --format '  {{.Service}}: {{.Status}}' || true
      return 1
    fi
    sleep 5
  done
}

summary() {
  local health
  health="$(curl -s --max-time 15 --resolve "$DOMAIN:443:127.0.0.1" "https://$DOMAIN/health" 2>/dev/null || true)"
  echo
  echo "  version     : ${1:0:12}"
  echo "  services    :"
  compose ps --format '                {{printf "%-13s" .Service}} {{.Status}}' || true
  echo "  /health     : $(sed -nE 's/.*"status" *: *"([A-Za-z]+)".*/\1/p' <<<"$health" | head -n1)  (face-service may need ~1 min after a fresh start)"
  echo "  certificate : $(echo | openssl s_client -connect 127.0.0.1:443 -servername "$DOMAIN" 2>/dev/null \
    | openssl x509 -noout -issuer -enddate 2>/dev/null | paste -sd';' || echo unknown)"
  echo
}

prune_images() {   # keep the images of the last $KEEP_VERSIONS deployed versions
  local keep img
  keep="$(tac "$STATE/history" 2>/dev/null | awk '!seen[$0]++' | head -n "$KEEP_VERSIONS" | paste -sd'|')"
  [[ -n "$keep" ]] || return 0
  while read -r img; do
    [[ "${img##*:}" =~ ^($keep)$ ]] || docker image rm "$img" >/dev/null 2>&1 || true
  done < <(docker image ls --format '{{.Repository}}:{{.Tag}}' | grep -E '^ghcr\.io/[^/]+/shaadilens-[a-z-]+:' || true)
  docker image prune -f >/dev/null 2>&1 || true
}

install_crontab() {   # the deploy user's cron jobs come from the repo (deploy/crontab), replaced as a whole
  local want="$APP_DIR/deploy/crontab"
  [[ -f "$want" ]] || return 0
  if ! diff -q <(crontab -l 2>/dev/null) "$want" >/dev/null 2>&1; then
    crontab "$want" && log "crontab updated from deploy/crontab"
  fi
}

# Switch to <sha> and wait for health. Returns 1 on failure (caller decides about rollback).
switch_to() {
  local sha="$1"
  log "config files → ${sha:0:12}"
  checkout "$sha" || return 1
  export IMAGE_TAG="$sha"
  # SHA tags never change, so only missing images are pulled. A rollback to a version whose images are still on
  # disk therefore works without a registry login (e.g. ./deploy/rollback.sh by hand on the server).
  log "starting containers (pulling images :${sha:0:12} if not on disk yet)"
  compose up -d --pull missing --remove-orphans --quiet-pull || return 1
  log "waiting for api, web, caddy and https://$DOMAIN/health/live (up to ${HEALTH_TIMEOUT}s)"
  wait_healthy
}

CURRENT="$(cat "$STATE/current" 2>/dev/null || true)"
ROLLING_BACK="${ROLLING_BACK:-0}"
RUNNING_NOW="${CURRENT:0:12}"
log "deploy ${TARGET:0:12} (running now: ${RUNNING_NOW:-nothing})"

if [[ "$ROLLING_BACK" != 1 ]]; then
  predeploy_dump "${CURRENT:-none}"
fi

if switch_to "$TARGET"; then
  if [[ -n "$CURRENT" && "$CURRENT" != "$TARGET" ]]; then echo "$CURRENT" > "$STATE/previous"; fi
  echo "$TARGET" > "$STATE/current"
  echo "$TARGET" >> "$STATE/history"
  if [[ "$ROLLING_BACK" == 1 ]]; then note=" (rollback)"; else note=""; fi
  echo "$(date -Is) deployed $TARGET$note" >> "$LOGS/deploy.log"
  install_crontab
  prune_images
  log "OK — ${TARGET:0:12} is live"
  summary "$TARGET"
  echo "DEPLOYED_URL=https://$DOMAIN"
  exit 0
fi

echo "$(date -Is) FAILED $TARGET" >> "$LOGS/deploy.log"
log "deploy of ${TARGET:0:12} FAILED — last log lines:"
compose logs --no-color --tail=40 api web caddy 2>/dev/null | sed 's/^/    /' || true

if [[ -n "$CURRENT" && "$CURRENT" != "$TARGET" && "$ROLLING_BACK" != 1 ]]; then
  log "automatic rollback to ${CURRENT:0:12}"
  ROLLING_BACK=1
  if switch_to "$CURRENT"; then
    echo "$(date -Is) auto-rollback to $CURRENT" >> "$LOGS/deploy.log"
    log "rolled back — ${CURRENT:0:12} is live again"
    summary "$CURRENT"
  else
    log "ROLLBACK FAILED TOO — check 'docker compose ps' and the logs on the server"
  fi
else
  log "nothing to roll back to"
fi
exit 1
