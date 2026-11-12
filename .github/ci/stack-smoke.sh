#!/usr/bin/env bash
# Whole-stack smoke test from pre-built images (CI uses it on the arm64 runner).
#   FACE_IMAGE=… API_IMAGE=… REEL_IMAGE=… WEB_IMAGE=… .github/ci/stack-smoke.sh
# Starts postgres + face-service + api + reel-worker + web, waits for /health = Healthy, then logs in as admin,
# creates an event, uploads insightface's sample group photo through the chunked upload API (via nginx) and waits
# until faces are detected. Prints only statuses and counts. Throwaway secrets, own compose project, cleans up.
set -euo pipefail

cd "$(dirname "$0")/../.."
PROJECT="${SMOKE_PROJECT:-shaadilens-smoke}"
PORT="${SMOKE_PORT:-18080}"
BASE="${SMOKE_BASE:-http://localhost:$PORT}"
WORK="$(mktemp -d)"
ENV_FILE="$WORK/smoke.env"

rand() { openssl rand -hex 32; }
cat > "$ENV_FILE" <<ENV
POSTGRES_PASSWORD=$(rand)
JWT_SECRET=$(rand)
MEDIA_SIGNING_KEY=$(rand)
ADMIN_EMAIL=smoke@example.com
ADMIN_PASSWORD=$(rand)
ASPNETCORE_ENVIRONMENT=Production
PUBLIC_BASE_URL=$BASE
WEB_PORT=$PORT
FACE_WORKERS=2
FACE_OMP_THREADS=2
REEL_CPUS=1
ENV
ADMIN_PASSWORD="$(grep '^ADMIN_PASSWORD=' "$ENV_FILE" | cut -d= -f2)"
if [[ -n "${GITHUB_ACTIONS:-}" ]]; then echo "::add-mask::$ADMIN_PASSWORD"; fi

compose() { docker compose -p "$PROJECT" --env-file "$ENV_FILE" -f docker-compose.yml -f .github/ci/compose.images.yml "$@"; }

cleanup() {
  local rc=$?
  if [[ $rc -ne 0 ]]; then
    echo "---- smoke test failed; service states and last log lines ----"
    compose ps -a || true
    compose logs --no-color --tail=60 api reel-worker face-service web || true
  fi
  compose down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

step() { printf '\n==> %s\n' "$*"; }

step "Start the stack ($(uname -m))"
compose up -d --no-build --quiet-pull

step "Wait for GET /health = Healthy (Postgres + face-service)"
status=""
for _ in $(seq 1 90); do
  status="$(curl -s "$BASE/health" | jq -r '.status // empty' 2>/dev/null || true)"
  [[ "$status" == "Healthy" ]] && break
  sleep 4
done
curl -s "$BASE/health" | jq -c '{status, checks: ((.checks // {}) | map_values(.status))}' || true
[[ "$status" == "Healthy" ]] || { echo "FAIL: /health is '$status'"; exit 1; }

step "Admin login, create event"
JAR="$WORK/cookies"
jq -n --arg e smoke@example.com --arg p "$ADMIN_PASSWORD" '{email:$e, password:$p}' \
  | curl -fsS -c "$JAR" -H 'Content-Type: application/json' -d @- "$BASE/api/v1/auth/login" >/dev/null
api() { curl -fsS -b "$JAR" "$@"; }
EVENT="$(api -H 'Content-Type: application/json' \
  -d '{"name":"ARM smoke test","watermarkEnabled":false,"faceSearchEnabled":true}' "$BASE/api/v1/events")"
EVENT_ID="$(jq -r .id <<<"$EVENT")"
COLLECTION_ID="$(jq -r '.collections[0].id' <<<"$EVENT")"
echo "event created with $(jq '.collections | length' <<<"$EVENT") collection(s)"

step "Chunked upload of the sample photo"
PHOTO="$WORK/t1.jpg"
compose cp face-service:/opt/venv/lib/python3.11/site-packages/insightface/data/images/t1.jpg "$PHOTO" >/dev/null
SIZE="$(stat -c %s "$PHOTO")"
INIT="$(jq -n --arg c "$COLLECTION_ID" --argjson s "$SIZE" '{collectionId:$c, fileName:"t1.jpg", size:$s}' \
  | api -H 'Content-Type: application/json' -d @- "$BASE/api/v1/events/$EVENT_ID/uploads")"
UPLOAD_ID="$(jq -r .uploadId <<<"$INIT")"
CHUNK="$(jq -r .chunkSize <<<"$INIT")"
TOTAL="$(jq -r .totalChunks <<<"$INIT")"
for ((i = 0; i < TOTAL; i++)); do
  dd if="$PHOTO" bs="$CHUNK" skip="$i" count=1 status=none \
    | api -X PUT -H 'Content-Type: application/octet-stream' --data-binary @- "$BASE/api/v1/uploads/$UPLOAD_ID/chunks/$i"
done
api -X POST "$BASE/api/v1/uploads/$UPLOAD_ID/complete" | jq -c '{status}'

step "Wait for processing (resize with Magick.NET, then face detection)"
stats=""
for _ in $(seq 1 60); do
  stats="$(api "$BASE/api/v1/events/$EVENT_ID/stats")"
  [[ "$(jq '.faces' <<<"$stats")" -ge 1 || "$(jq '.failed' <<<"$stats")" -ge 1 ]] && break
  sleep 3
done
jq -c '{photos, processed, failed, faces, byStatus}' <<<"$stats"
[[ "$(jq '.failed' <<<"$stats")" -eq 0 && "$(jq '.faces' <<<"$stats")" -ge 1 ]] \
  || { echo "FAIL: photo was not processed into faces"; exit 1; }

step "Thumbnail is served"
THUMB="$(api "$BASE/api/v1/events/$EVENT_ID/photos" | jq -r '.items[0].thumbUrl')"
code="$(curl -s -o /dev/null -w '%{http_code}' -b "$JAR" "$BASE$THUMB")"
echo "GET thumbnail -> HTTP $code"
[[ "$code" == "200" ]] || { echo "FAIL: thumbnail not served"; exit 1; }

compose ps --format '{{.Service}}: {{.Status}}'
echo
echo "Stack smoke test passed on $(uname -m)."
