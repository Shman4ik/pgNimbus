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
#   publish_size_mb  total size of the shipped files in the AOT publish dir
#                     (exe + native deps like libSkiaSharp/libHarfBuzzSharp),
#                     excluding *.pdb/*.dbg debug symbols — the same exclusion
#                     the MSI (Product.wxs) and MSIX (build-msix.ps1) apply,
#                     so this tracks what installers actually package
#   connect_ms       first physical connection on a cold pool
#   roundtrip_ms     SELECT 1 on a warm pooled connection (median)
#   first_batch_ms   large SELECT: call → first streamed RowBatch (median)
#   stream_ms        large SELECT: full drain through the streaming path (median)
#
# UI-thread metrics (the 2026-09 UI-thread audit, docs/dev/design/ui-thread-audit.md),
# every one the median of a few runs and smaller-is-better:
#   stage_deletes_ms, copy_tsv_ms, history_append_ms, editor_statement_ms
#                    Core work that used to grow with the data on the UI thread
#                    (PgNimbus.Benchmarks, in process, no server needed)
#   ui_*             the views themselves over big data: a 5,000-table schema, a
#                    5,000-statement script, 50,000 relations in the palette, a
#                    result as wide as the grid shows, a megabyte of plan text, a
#                    key typed at the end of a 5 MB script (tools/UiBench, headless
#                    Avalonia, no display needed); *_rows / *_chips count the rows
#                    a view realized
#
# Startup numbers come from the app itself (PGNIMBUS_STARTUP_PROBE=1 prints
# launch-to-first-frame and exits — see src/PgNimbus.App/StartupProbe.cs); query
# numbers come from the PgNimbus.Benchmarks console project.
#
# Environment:
#   PGNIMBUS_BENCH_CONN   connection string (default: localhost/postgres/postgres)
#   PGNIMBUS_BENCH_RUNS   startup samples per mode, median reported (default 7)
#   PGNIMBUS_BENCH_ROWS   row count for the streaming benchmarks (default 100000)
#   PGNIMBUS_BENCH_PUBLISH_DIR  an existing linux-x64 NativeAOT publish dir to
#                     measure instead of publishing one here (the release
#                     pipeline reuses build-linux's output this way; must
#                     contain PgNimbus.App and its side-car native libs)
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
JIT_BINARY=src/PgNimbus.App/bin/Release/net10.0/PgNimbus.App

if [[ -n "${PGNIMBUS_BENCH_PUBLISH_DIR:-}" ]]; then
    echo "== Using prebuilt NativeAOT publish at $PGNIMBUS_BENCH_PUBLISH_DIR"
    PUBLISH_DIR="$PGNIMBUS_BENCH_PUBLISH_DIR"
    AOT_BINARY="$PUBLISH_DIR/PgNimbus.App"
    [[ -f "$AOT_BINARY" ]] || { echo "error: no PgNimbus.App in $PUBLISH_DIR" >&2; exit 1; }
    # CI hands the publish over as an artifact; artifact zips drop the exec
    # bit (the producer tars to preserve it), so restore it defensively.
    [[ -x "$AOT_BINARY" ]] || chmod +x "$AOT_BINARY"
elif [[ -z "${PGNIMBUS_BENCH_SKIP_AOT:-}" ]]; then
    echo "== Publishing (NativeAOT linux-x64) — this is the slow part"
    AOT_BINARY=src/PgNimbus.App/bin/Release/net10.0/linux-x64/publish/PgNimbus.App
    PUBLISH_DIR=$(dirname "$AOT_BINARY")
    # dotnet publish never cleans its output dir, so a repeated local run
    # would keep (and count) files a previous build no longer produces.
    rm -rf "$PUBLISH_DIR"
    dotnet publish src/PgNimbus.App -c Release -r linux-x64 -p:PublishAot=true >/dev/null
fi

if [[ -n "${AOT_BINARY:-}" ]]; then
    BINARY_SIZE_MB=$(awk "BEGIN { printf \"%.1f\", $(stat -c%s "$AOT_BINARY") / 1024 / 1024 }")
    # Measure what ships, not what publish leaves on disk: the MSI and MSIX
    # both exclude debug symbols (*.pdb — see packaging/windows/Product.wxs
    # and scripts/windows/build-msix.ps1); the linux-x64 equivalent is the
    # *.dbg file NativeAOT strips symbols into. Counting them here would
    # make the metric miss packaging-size changes entirely.
    PUBLISH_SIZE_BYTES=$(find "$PUBLISH_DIR" -type f ! -name '*.pdb' ! -name '*.dbg' -printf '%s\n' \
        | awk '{ s += $1 } END { print s }')
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
    dotnet run --project tests/PgNimbus.Benchmarks -c Release --no-build)
echo "$QUERY_OUT"
bench_value() { grep -o "PGNIMBUS_BENCH $1=[0-9.]*" <<<"$QUERY_OUT" | cut -d= -f2; }
CONNECT_MS=$(bench_value connect_ms)
ROUNDTRIP_MS=$(bench_value roundtrip_ms)
FIRST_BATCH_MS=$(bench_value first_batch_ms)
STREAM_MS=$(bench_value stream_ms)
ROWS_PER_SEC=$(bench_value rows_per_sec)
SCRIPT_MS=$(bench_value script_ms)
BATCH_APPLY_MS=$(bench_value batch_apply_ms)
STAGE_DELETES_MS=$(bench_value stage_deletes_ms)
COPY_TSV_MS=$(bench_value copy_tsv_ms)
HISTORY_APPEND_MS=$(bench_value history_append_ms)
HISTORY_LOAD_MS=$(bench_value history_load_ms)
EDITOR_STATEMENT_MS=$(bench_value editor_statement_ms)

# --- views over big data (headless) -----------------------------------------
echo "== UI thread (headless views)"
UI_OUT=$(dotnet run --project tools/UiBench -c Release --no-build)
echo "$UI_OUT"
ui_value() { grep -o "PGNIMBUS_BENCH $1=[0-9.]*" <<<"$UI_OUT" | cut -d= -f2; }
UI_SCHEMA_EXPAND_MS=$(ui_value ui_schema_expand_ms)
UI_SCHEMA_EXPAND_ROWS=$(ui_value ui_schema_expand_rows)
UI_SCHEMA_FILTER_MS=$(ui_value ui_schema_filter_ms)
UI_SCRIPT_SECTIONS_MS=$(ui_value ui_script_sections_ms)
UI_SCRIPT_SECTION_CHIPS=$(ui_value ui_script_section_chips)
UI_PALETTE_KEY_MS=$(ui_value ui_palette_key_ms)
UI_WIDE_RESULT_MS=$(ui_value ui_wide_result_ms)
UI_EDIT_CONTEXT_MS=$(ui_value ui_edit_context_ms)
UI_LARGE_TEXT_MS=$(ui_value ui_large_text_ms)
UI_EDITOR_KEY_MS=$(ui_value ui_editor_key_ms)
UI_HISTORY_RECORD_MS=$(ui_value ui_history_record_ms)

# --- report ------------------------------------------------------------------
# JSON in github-action-benchmark's "customSmallerIsBetter" format.
{
    echo "["
    [[ -n "${AOT_BINARY:-}" ]] && cat <<EOF
  { "name": "Startup, launch to first frame (NativeAOT)", "unit": "ms", "value": $STARTUP_AOT_MS },
  { "name": "Memory at first frame (NativeAOT)", "unit": "MB", "value": $RSS_AOT_MB },
  { "name": "Binary size (NativeAOT)", "unit": "MB", "value": $BINARY_SIZE_MB },
  { "name": "Publish size (NativeAOT, shipped files)", "unit": "MB", "value": $PUBLISH_SIZE_MB },
EOF
    cat <<EOF
  { "name": "Startup, launch to first frame (JIT)", "unit": "ms", "value": $STARTUP_JIT_MS },
  { "name": "Connect, cold pool", "unit": "ms", "value": $CONNECT_MS },
  { "name": "Round-trip, SELECT 1 warm", "unit": "ms", "value": $ROUNDTRIP_MS },
  { "name": "First row batch of a $ROWS-row SELECT", "unit": "ms", "value": $FIRST_BATCH_MS },
  { "name": "Stream $ROWS rows", "unit": "ms", "value": $STREAM_MS },
  { "name": "Run a 200-statement seed script", "unit": "ms", "value": $SCRIPT_MS },
  { "name": "Commit 1,000 staged edits", "unit": "ms", "value": $BATCH_APPLY_MS },
  { "name": "Stage 100,000 deletes (safe mode)", "unit": "ms", "value": $STAGE_DELETES_MS },
  { "name": "Copy 100,000 rows as TSV", "unit": "ms", "value": $COPY_TSV_MS },
  { "name": "Write the history after a run (200 entries, 5 MB)", "unit": "ms", "value": $HISTORY_APPEND_MS },
  { "name": "Load the history (200 entries, 5 MB)", "unit": "ms", "value": $HISTORY_LOAD_MS },
  { "name": "Find the caret's statement in a 5 MB script, per key", "unit": "ms", "value": $EDITOR_STATEMENT_MS },
  { "name": "UI: expand a schema of 5,000 tables", "unit": "ms", "value": $UI_SCHEMA_EXPAND_MS },
  { "name": "UI: tree rows realized for 5,000 tables", "unit": "rows", "value": $UI_SCHEMA_EXPAND_ROWS },
  { "name": "UI: filter 5,000 tables to one", "unit": "ms", "value": $UI_SCHEMA_FILTER_MS },
  { "name": "UI: 5,000 script sections land", "unit": "ms", "value": $UI_SCRIPT_SECTIONS_MS },
  { "name": "UI: script section chips realized", "unit": "chips", "value": $UI_SCRIPT_SECTION_CHIPS },
  { "name": "UI: palette keystroke over 50,000 relations", "unit": "ms", "value": $UI_PALETTE_KEY_MS },
  { "name": "UI: widest result the grid shows", "unit": "ms", "value": $UI_WIDE_RESULT_MS },
  { "name": "UI: edit context arriving after the rows", "unit": "ms", "value": $UI_EDIT_CONTEXT_MS },
  { "name": "UI: show 1 MB of read-only text", "unit": "ms", "value": $UI_LARGE_TEXT_MS },
  { "name": "UI: key typed at the end of a 5 MB script", "unit": "ms", "value": $UI_EDITOR_KEY_MS },
  { "name": "UI: record a run with a full history", "unit": "ms", "value": $UI_HISTORY_RECORD_MS }
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
        echo "| Publish size (NativeAOT, shipped files) | $PUBLISH_SIZE_MB MB |"
    }
    echo "| Startup, launch → first frame (JIT) | $STARTUP_JIT_MS ms |"
    echo "| Connect (cold pool) | $CONNECT_MS ms |"
    echo "| Round-trip (\`SELECT 1\`, warm) | $ROUNDTRIP_MS ms |"
    echo "| First row batch of a $ROWS-row SELECT | $FIRST_BATCH_MS ms |"
    echo "| Stream $ROWS rows | $STREAM_MS ms ($ROWS_PER_SEC rows/s) |"
    echo "| Run a 200-statement seed script | $SCRIPT_MS ms |"
    echo "| Commit 1,000 staged edits | $BATCH_APPLY_MS ms |"
    echo
    echo "#### UI thread over big data"
    echo
    echo "| Metric | Value |"
    echo "| --- | ---: |"
    echo "| Stage 100,000 deletes (safe mode) | $STAGE_DELETES_MS ms |"
    echo "| Copy 100,000 rows as TSV | $COPY_TSV_MS ms |"
    echo "| Write the history after a run (200 entries, 5 MB) | $HISTORY_APPEND_MS ms |"
    echo "| Load the history (200 entries, 5 MB) | $HISTORY_LOAD_MS ms |"
    echo "| Find the caret's statement in a 5 MB script, per key | $EDITOR_STATEMENT_MS ms |"
    echo "| Expand a schema of 5,000 tables | $UI_SCHEMA_EXPAND_MS ms ($UI_SCHEMA_EXPAND_ROWS rows realized) |"
    echo "| Filter 5,000 tables to one | $UI_SCHEMA_FILTER_MS ms |"
    echo "| 5,000 script sections land | $UI_SCRIPT_SECTIONS_MS ms ($UI_SCRIPT_SECTION_CHIPS chips realized) |"
    echo "| Palette keystroke over 50,000 relations | $UI_PALETTE_KEY_MS ms |"
    echo "| Widest result the grid shows | $UI_WIDE_RESULT_MS ms |"
    echo "| Edit context arriving after the rows | $UI_EDIT_CONTEXT_MS ms |"
    echo "| Show 1 MB of read-only text | $UI_LARGE_TEXT_MS ms |"
    echo "| Key typed at the end of a 5 MB script | $UI_EDITOR_KEY_MS ms |"
    echo "| Record a run with a full history | $UI_HISTORY_RECORD_MS ms |"
    echo
    echo "Startup is the median of $RUNS runs, measured inside the app from OS process"
    echo "start to the first rendered frame; query metrics are medians via"
    echo "\`PgNimbus.Benchmarks\` against a local PostgreSQL. UI-thread metrics are"
    echo "medians from \`tools/UiBench\` on Avalonia's headless platform (software"
    echo "rendering), so they are machine-relative: read them as a trend."
} >"$OUT_DIR/summary.md"

cat "$OUT_DIR/summary.md"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    cat "$OUT_DIR/summary.md" >>"$GITHUB_STEP_SUMMARY"
fi
