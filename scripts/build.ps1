<#
.SYNOPSIS
  Build + test everything. Uses Docker for each toolchain, so it works on any machine
  with Docker Desktop (no local .NET / Node / Python needed).

.EXAMPLE
  ./scripts/build.ps1                 # all
  ./scripts/build.ps1 -Only backend   # backend | frontend | face
#>
param(
    [ValidateSet('all', 'backend', 'frontend', 'face')]
    [string]$Only = 'all'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Invoke-Step([string]$name, [scriptblock]$block) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)" }
}

if ($Only -in 'all', 'backend') {
    Invoke-Step 'backend: build + test (.NET 10)' {
        # Docker socket + host override: Testcontainers starts a real Postgres next to the SDK container.
        docker run --rm -v //var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal `
            -v "${root}/backend:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 `
            bash -c 'dotnet build ShaadiLens.sln -c Release -nologo && dotnet test ShaadiLens.sln -c Release --no-build -nologo'
    }
}

if ($Only -in 'all', 'frontend') {
    Invoke-Step 'frontend: test + build (Angular, Node 24)' {
        # Copy sources into the container so Linux node_modules never land in your Windows folder.
        docker run --rm -e CI=true -e NG_CLI_ANALYTICS=false -v "${root}/frontend/shaadilens-web:/src:ro" node:24 `
            bash -c 'mkdir /work && cd /src && tar --exclude=node_modules --exclude=.angular --exclude=dist -cf - . | tar -xf - -C /work && cd /work && npm ci --no-audit --no-fund && npx ng test --watch=false && npx ng build'
    }
}

if ($Only -in 'all', 'face') {
    Invoke-Step 'face-service: image build' {
        docker compose -f "$root/docker-compose.yml" build face-service
    }
    Invoke-Step 'face-service: pytest' {
        docker run --rm shaadilens/face-service:dev pytest -q
    }
}

Write-Host "`nAll good." -ForegroundColor Green
