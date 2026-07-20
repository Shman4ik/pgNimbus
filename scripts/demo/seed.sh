#!/usr/bin/env bash
# Loads the pgNimbus type-rich demo dataset into a PostgreSQL database by
# running 01..05 in order with psql. Idempotent: each run drops and recreates
# the demo schemas, so it always ends in the same canonical state.
#
# Usage:
#   ./seed.sh "postgres://user:pass@host:5432/dbname?sslmode=require"
#   ./seed.sh                      # use libpq PG* env vars (PGHOST, PGDATABASE, ...)
#
# Requires: psql on PATH, and a PostgreSQL 14+ server where the connecting
# role may CREATE EXTENSION (citext, hstore, pgcrypto, pg_trgm, ltree, vector).
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONN="${1:-}"

run() {
    if [[ -n "$CONN" ]]; then
        psql "$CONN" -v ON_ERROR_STOP=1 -q -f "$1"
    else
        psql -v ON_ERROR_STOP=1 -q -f "$1"
    fi
}

for f in 01_public 02_commerce 03_iot 04_org 05_analytics; do
    echo ">>> ${f}.sql"
    run "$DIR/${f}.sql"
done

echo "Done. pgNimbus demo data loaded."
