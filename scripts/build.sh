#!/usr/bin/env bash
# Build + test everything using Docker toolchains (Linux/macOS/CI).
# Usage: ./scripts/build.sh [all|backend|frontend|face]
set -euo pipefail
ONLY="${1:-all}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

step() { echo; echo "=== $1 ==="; }

if [[ "$ONLY" == all || "$ONLY" == backend ]]; then
  step "backend: build + test (.NET 10)"
  docker run --rm -v "$ROOT/backend:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -c 'dotnet build ShaadiLens.sln -c Release -nologo && dotnet test ShaadiLens.sln -c Release --no-build -nologo'
fi

if [[ "$ONLY" == all || "$ONLY" == frontend ]]; then
  step "frontend: test + build (Angular, Node 24)"
  docker run --rm -e CI=true -e NG_CLI_ANALYTICS=false -v "$ROOT/frontend/shaadilens-web:/src:ro" node:24 \
    bash -c 'mkdir /work && cd /src && tar --exclude=node_modules --exclude=.angular --exclude=dist -cf - . | tar -xf - -C /work && cd /work && npm ci --no-audit --no-fund && npx ng test --watch=false && npx ng build'
fi

if [[ "$ONLY" == all || "$ONLY" == face ]]; then
  step "face-service: image build + pytest"
  docker compose -f "$ROOT/docker-compose.yml" build face-service
  docker run --rm shaadilens/face-service:dev pytest -q
fi

echo; echo "All good."
