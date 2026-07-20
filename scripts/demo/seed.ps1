#!/usr/bin/env pwsh
# Loads the pgNimbus type-rich demo dataset into a PostgreSQL database by
# running 01..05 in order with psql. Idempotent: each run drops and recreates
# the demo schemas, so it always ends in the same canonical state.
#
# Usage:
#   ./seed.ps1 "postgres://user:pass@host:5432/dbname?sslmode=require"
#   ./seed.ps1                     # use libpq PG* env vars (PGHOST, PGDATABASE, ...)
#
# Requires: psql on PATH, and a PostgreSQL 14+ server where the connecting
# role may CREATE EXTENSION (citext, hstore, pgcrypto, pg_trgm, ltree, vector).
param([string]$ConnectionString)

$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

foreach ($f in '01_public','02_commerce','03_iot','04_org','05_analytics') {
    Write-Host ">>> $f.sql"
    $file = Join-Path $dir "$f.sql"
    if ($ConnectionString) {
        psql $ConnectionString -v ON_ERROR_STOP=1 -q -f $file
    } else {
        psql -v ON_ERROR_STOP=1 -q -f $file
    }
    if ($LASTEXITCODE -ne 0) { throw "psql failed on $f.sql (exit $LASTEXITCODE)" }
}

Write-Host "Done. pgNimbus demo data loaded."
