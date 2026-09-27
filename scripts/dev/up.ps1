$ErrorActionPreference = 'Stop'
Set-Location (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
if (-not (Test-Path .env)) { Write-Error "Missing .env. Copy: Copy-Item .env.example .env"; exit 1 }
docker compose up -d --build
