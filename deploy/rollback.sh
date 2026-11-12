#!/usr/bin/env bash
# ShaadiLens — go back to an earlier version.
#
#   ./deploy/rollback.sh            back to the version that ran before the current one (state/previous)
#   ./deploy/rollback.sh <sha>      back to a specific deployed version (see /opt/shaadilens/state/history)
#
# Uses the images still on the server (deploy.sh keeps the last 3 versions), so it works without a registry
# login. For older versions use GitHub → Actions → "Deploy" → Run workflow with the sha (it logs in to pull).
#
# Database: migrations only go forward. If the version you leave added a migration, the old code usually still
# works with the newer schema. If it doesn't, restore the dump taken right before that deploy:
#   ls -t /opt/shaadilens/backups/db/predeploy-*        (the name ends with the first 8 chars of the old sha)
# and follow "Restore a database dump" in docs/DEPLOY.md.
set -euo pipefail

STATE="${APP_ROOT:-/opt/shaadilens}/state"
TARGET="${1:-$(cat "$STATE/previous" 2>/dev/null || true)}"
[[ -n "$TARGET" ]] || { echo "No previous version recorded in $STATE/previous; pass a sha." >&2; exit 2; }

if [[ "$TARGET" =~ ^[0-9a-f]{7,39}$ ]]; then   # short sha → full sha from the deploy history
  full="$(grep -E "^$TARGET" "$STATE/history" 2>/dev/null | tail -n1 || true)"
  [[ -n "$full" ]] || { echo "$TARGET is not in $STATE/history; pass the full 40-character sha." >&2; exit 2; }
  TARGET="$full"
fi

echo "Rolling back to ${TARGET:0:12} (current: $(cut -c1-12 "$STATE/current" 2>/dev/null || echo unknown))"
exec "$(dirname "$(readlink -f "$0")")/deploy.sh" "$TARGET"
