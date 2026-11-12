#!/usr/bin/env bash
# Tells DuckDNS this server's current public IP. Run by cron every 5 minutes (see bootstrap.sh).
# Does nothing until DUCKDNS_SUBDOMAIN and DUCKDNS_TOKEN are set in the .env file.
# The token is passed to curl on stdin, so it never appears in the process list or in the log.
set -euo pipefail

ENV_FILE="${ENV_FILE:-/opt/shaadilens/.env}"

# Read one KEY=value line from .env without executing the file.
env_get() { grep -E "^$1=" "$ENV_FILE" | tail -n1 | cut -d= -f2- | tr -d '\r' | sed -e 's/^"//' -e 's/"$//'; }

[[ -r "$ENV_FILE" ]] || { echo "$(date -Is) cannot read $ENV_FILE"; exit 1; }
SUB="$(env_get DUCKDNS_SUBDOMAIN || true)"
TOKEN="$(env_get DUCKDNS_TOKEN || true)"

if [[ -z "$SUB" || -z "$TOKEN" || "$SUB" == *CHANGE_ME* || "$TOKEN" == *CHANGE_ME* ]]; then
  exit 0   # not configured yet
fi

# Empty ip= lets DuckDNS use the address the request comes from (this server's public IP).
RESULT="$(printf 'url = "https://www.duckdns.org/update?domains=%s&token=%s&ip="\n' "$SUB" "$TOKEN" \
  | curl -fsS --max-time 20 --config - || echo "curl-failed")"

# Log only changes and failures, so the log stays tiny.
STATE_FILE="${STATE_FILE:-/opt/shaadilens/state/duckdns.last}"
if [[ "$RESULT" != "OK" ]]; then
  echo "$(date -Is) DuckDNS update FAILED for $SUB (response: ${RESULT:0:20})"
  exit 1
fi
if [[ "$(cat "$STATE_FILE" 2>/dev/null || true)" != "OK" ]]; then
  echo "$(date -Is) DuckDNS update OK for $SUB.duckdns.org"
fi
echo "OK" > "$STATE_FILE"
