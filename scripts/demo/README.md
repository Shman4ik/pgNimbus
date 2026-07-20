# pgNimbus demo data

A type-rich, multi-schema PostgreSQL dataset (~46 MB) for exploring and
demoing pgNimbus. It's built to exercise the PostgreSQL-first parts of the
UI — the schema tree, relation sizes, the Database Overview panel, and the
result-grid / cell-inspector rendering of exotic types.

## What it creates

Five schemas, loaded in order:

| # | Schema | Highlights | Types on display |
|---|--------|-----------|------------------|
| 01 | `public` | The simple starter shop (~500 customers, 200 products, 2 000 orders) + an `updated_at` trigger. Also enables the extensions the other files need. | the everyday types |
| 02 | `commerce` | The showcase — 800 customers, 300 products, 3 000 orders, ~7 500 line items, 2 000 reviews. | **enum**, **composite type** (`address`), **domain** (email over citext), uuid, `text[]`, **jsonb**, **hstore**, inet/cidr, `point`/`box`, **daterange/numrange/tstzrange**, **money**, interval, bytea, **`vector(3)`** (pgvector), generated + **tsvector** columns |
| 03 | `iot` | 120 devices + **200 000 readings** in a monthly **RANGE-partitioned** table (8 partitions, ~33 MB — the largest relation). | macaddr, `bit`/`varbit`, polygon, partition tree |
| 04 | `org` | An **`ltree`** org hierarchy (14 units) + 309 employees. | `int4range` salary bands, self-referencing `manager_id` |
| 05 | `analytics` | 2 views, 2 materialized views, sql/plpgsql **functions**, then `REFRESH` + `ANALYZE`. | — |

Required extensions (created by `01_public.sql`): `citext`, `hstore`,
`pgcrypto`, `pg_trgm`, `ltree`, `vector` (pgvector). The connecting role must
be allowed to `CREATE EXTENSION`.

## Running it

Point it at any PostgreSQL 14+ database (a throwaway local DB, a Docker
`postgres`, or a hosted one like Neon). **It drops and recreates the demo
schemas and the `public.*` demo tables**, so use a database you don't mind
overwriting.

```bash
# macOS / Linux
./seed.sh "postgres://user:pass@host:5432/dbname?sslmode=require"

# Windows / PowerShell
./seed.ps1 "postgres://user:pass@host:5432/dbname?sslmode=require"
```

Omit the argument to fall back to libpq's `PG*` environment variables
(`PGHOST`, `PGDATABASE`, `PGUSER`, `PGPASSWORD`, ...). You can also run the
files by hand, in order:

```bash
for f in 01_public 02_commerce 03_iot 04_org 05_analytics; do
    psql "$CONN" -v ON_ERROR_STOP=1 -f "$f.sql"
done
```

The scripts are idempotent — re-running always lands in the same state.

## A note on the randomness pattern

The data is generated with `random()`, but **not** by picking a random
related row with an uncorrelated `CROSS JOIN LATERAL (SELECT ... ORDER BY
random() LIMIT 1)`. PostgreSQL is free to evaluate that subquery once and
reuse a single result for every row, which silently collapses all the
variety (every order to one customer, every reading to one metric). Instead,
every `random()` call lives in a subquery target list over `generate_series`
(evaluated per row), and related rows are chosen by indexing into an
`array_agg` of candidate ids. Keep that pattern if you extend these scripts.
