#!/usr/bin/env bash
# Measures pgNimbus's headline performance numbers and writes them to
# bench-results/ as JSON (for history tracking) + Markdown (for humans).
# Run by .github/workflows/benchmark.yml on every PR and push to main;
# also runs locally on any Linux box with the .NET 10 SDK, Xvfb, and a
# reachable PostgreSQL.
#
# Metrics:
#   startup_aot_ms   launch → first rendered frame, NativeAOT Release binary
#   startup_jit_ms   launch → first rendered frame, JIT Release build
#   startup_rss_mb   resident memory at first frame (AOT)
#   binary_size_mb   size of the AOT executable alone
#   publish_size_mb  total size of the AOT publish dir (exe + native deps
#                     like libSkiaSharp/libHarfBuzzSharp) — what actually
#                     ships, since those side-car libs dwarf the exe itself
#   connect_ms       first physical connection on a cold pool
#   roundtrip_ms     SELECT 1 on a warm pooled connection (median)
#   first_batch_ms   large SELECT: call → first streamed RowBatch (median)
#   stream_ms        large SELECT: full drain through the streaming path (median)
#
# Startup numbers come from the app itself (PGNIMBUS_STARTUP_PROBE=1 prints
# launch-to-first-frame and exits — see PgNimbus.App/StartupProbe.cs); query
# numbers come from the PgNimbus.Benchmarks console project.
#
# Environment:
#   PGNIMBUS_BENCH_CONN   connection string (default: localhost/postgres/postgres)
#   PGNIMBUS_BENCH_RUNS   startup samples per mode, median reported (default 7)
#   PGNIMBUS_BENCH_ROWS   row count for the streaming benchmarks (default 100000)
#   PGNIMBUS_BENCH_SKIP_AOT=1   skip the AOT publish + AOT metrics (quick local runs)

set -euo pipefail
cd "$(dirname "$0")/../.."

CONN="${PGNIMBUS_BENCH_CONN:-Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres}"
RUNS="${PGNIMBUS_BENCH_RUNS:-7}"
ROWS="${PGNIMBUS_BENCH_ROWS:-100000}"
OUT_DIR="bench-results"
mkdir -p "$OUT_DIR"

if [[ "$(uname -s)" != "Linux" ]]; then
    echo "error: this script measures the linux-x64 build and only runs on Linux" >&2
    exit 1
fi

# --- a display for the startup runs (the app needs one to open a window) ----
if [[ -z "${DISPLAY:-}" ]]; then
    if [[ ! -e /tmp/.X99-lock ]]; then
        Xvfb :99 -screen 0 1280x800x24 &
        XVFB_PID=$!
        trap 'kill "$XVFB_PID" 2>/dev/null || true' EXIT
        sleep 1
    fi
    export DISPLAY=:99
fi

median() { # median of newline-separated numbers on stdin
    sort -n | awk '{ a[NR] = $1 } END { if (NR % 2) print a[(NR + 1) / 2]; else printf "%.1f\n", (a[NR / 2] + a[NR / 2 + 1]) / 2 }'
}

# Runs the given app binary $RUNS times under the startup probe and prints
# "<median window_ms> <median rss_bytes>".
measure_startup() {
    local binary=$1 times=() rss=()
    # One discarded warm-up run: the first-ever launch pays cold page/font
    # caches and can be 3x the steady state.
    PGNIMBUS_STARTUP_PROBE=1 PGNIMBUS_CONN="$CONN" timeout 120 "$binary" >/dev/null 2>&1
    for _ in $(seq "$RUNS"); do
        local line
        line=$(PGNIMBUS_STARTUP_PROBE=1 PGNIMBUS_CONN="$CONN" timeout 120 "$binary" 2>/dev/null \
            | grep -o 'PGNIMBUS_STARTUP_PROBE window_ms=[0-9.]* rss_bytes=[0-9]*')
        times+=("$(sed 's/.*window_ms=\([0-9.]*\).*/\1/' <<<"$line")")
        rss+=("$(sed 's/.*rss_bytes=\([0-9]*\).*/\1/' <<<"$line")")
    done
    echo "$(printf '%s\n' "${times[@]}" | median) $(printf '%s\n' "${rss[@]}" | median)"
}

# --- builds ------------------------------------------------------------------
echo "== Building (JIT Release)"
dotnet build -c Release >/dev/null
JIT_BINARY=PgNimbus.App/bin/Release/net10.0/PgNimbus.App

if [[ -z "${PGNIMBUS_BENCH_SKIP_AOT:-}" ]]; then
    echo "== Publishing (NativeAOT linux-x64) — this is the slow part"
    dotnet publish PgNimbus.App -c Release -r linux-x64 -p:PublishAot=true >/dev/null
    AOT_BINARY=PgNimbus.App/bin/Release/net10.0/linux-x64/publish/PgNimbus.App
    BINARY_SIZE_MB=$(awk "BEGIN { printf \"%.1f\", $(stat -c%s "$AOT_BINARY") / 1024 / 1024 }")
    PUBLISH_DIR=$(dirname "$AOT_BINARY")
    PUBLISH_SIZE_BYTES=$(du -sb "$PUBLISH_DIR" | cut -f1)
    PUBLISH_SIZE_MB=$(awk "BEGIN { printf \"%.1f\", $PUBLISH_SIZE_BYTES / 1024 / 1024 }")
fi

# --- startup -----------------------------------------------------------------
if [[ -n "${AOT_BINARY:-}" ]]; then
    echo "== Startup (AOT), $RUNS runs"
    read -r STARTUP_AOT_MS RSS_AOT_BYTES <<<"$(measure_startup "$AOT_BINARY")"
    RSS_AOT_MB=$(awk "BEGIN { printf \"%.1f\", $RSS_AOT_BYTES / 1024 / 1024 }")
fi

echo "== Startup (JIT), $RUNS runs"
read -r STARTUP_JIT_MS _ <<<"$(measure_startup "$JIT_BINARY")"

# --- query engine -------------------------------------------------------------
echo "== Query engine ($ROWS-row stream)"
QUERY_OUT=$(PGNIMBUS_BENCH_CONN="$CONN" PGNIMBUS_BENCH_ROWS="$ROWS" \
    dotnet run --project PgNimbus.Benchmarks -c Release --no-build)
echo "$QUERY_OUT"
bench_value() { grep -o "PGNIMBUS_BENCH $1=[0-9.]*" <<<"$QUERY_OUT" | cut -d= -f2; }
CONNECT_MS=$(bench_value connect_ms)
ROUNDTRIP_MS=$(bench_value roundtrip_ms)
FIRST_BATCH_MS=$(bench_value first_batch_ms)
STREAM_MS=$(bench_value stream_ms)
ROWS_PER_SEC=$(bench_value rows_per_sec)

# --- report ------------------------------------------------------------------
# JSON in github-action-benchmark's "customSmallerIsBetter" format.
{
    echo "["
    [[ -n "${AOT_BINARY:-}" ]] && cat <<EOF
  { "name": "Startup, launch to first frame (NativeAOT)", "unit": "ms", "value": $STARTUP_AOT_MS },
  { "name": "Memory at first frame (NativeAOT)", "unit": "MB", "value": $RSS_AOT_MB },
  { "name": "Binary size (NativeAOT)", "unit": "MB", "value": $BINARY_SIZE_MB },
  { "name": "Publish size (NativeAOT, all files)", "unit": "MB", "value": $PUBLISH_SIZE_MB },
EOF
    cat <<EOF
  { "name": "Startup, launch to first frame (JIT)", "unit": "ms", "value": $STARTUP_JIT_MS },
  { "name": "Connect, cold pool", "unit": "ms", "value": $CONNECT_MS },
  { "name": "Round-trip, SELECT 1 warm", "unit": "ms", "value": $ROUNDTRIP_MS },
  { "name": "First row batch of a $ROWS-row SELECT", "unit": "ms", "value": $FIRST_BATCH_MS },
  { "name": "Stream $ROWS rows", "unit": "ms", "value": $STREAM_MS }
]
EOF
} >"$OUT_DIR/benchmarks.json"

{
    echo "### pgNimbus benchmarks"
    echo
    echo "| Metric | Value |"
    echo "| --- | ---: |"
    [[ -n "${AOT_BINARY:-}" ]] && {
        echo "| Startup, launch → first frame (NativeAOT) | $STARTUP_AOT_MS ms |"
        echo "| Memory at first frame (NativeAOT) | $RSS_AOT_MB MB |"
        echo "| Binary size (NativeAOT) | $BINARY_SIZE_MB MB |"
        echo "| Publish size (NativeAOT, all files) | $PUBLISH_SIZE_MB MB |"
    }
    echo "| Startup, launch → first frame (JIT) | $STARTUP_JIT_MS ms |"
    echo "| Connect (cold pool) | $CONNECT_MS ms |"
    echo "| Round-trip (\`SELECT 1\`, warm) | $ROUNDTRIP_MS ms |"
    echo "| First row batch of a $ROWS-row SELECT | $FIRST_BATCH_MS ms |"
    echo "| Stream $ROWS rows | $STREAM_MS ms ($ROWS_PER_SEC rows/s) |"
    echo
    echo "Startup is the median of $RUNS runs, measured inside the app from OS process"
    echo "start to the first rendered frame; query metrics are medians via"
    echo "\`PgNimbus.Benchmarks\` against a local PostgreSQL."
} >"$OUT_DIR/summary.md"

cat "$OUT_DIR/summary.md"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    cat "$OUT_DIR/summary.md" >>"$GITHUB_STEP_SUMMARY"
fi
