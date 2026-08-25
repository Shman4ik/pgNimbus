#!/usr/bin/env bash
#
# Launches a built pgNimbus and asserts it reaches a rendered window.
#
# This is the release gate that unit tests structurally cannot be: it runs the
# artifact that will actually be downloaded. Everything it catches lives past
# the compiler — NativeAOT trimming a type the app needs at runtime, an asset
# missing from the package, a .deb whose Depends list forgot a library the X11
# backend loads, a code-signing or bundle-layout mistake that stops the binary
# from starting at all.
#
# Mechanism: PGNIMBUS_STARTUP_PROBE=1 makes the app print one line after its
# first window has rendered its first frame and then exit (see
# PgNimbus.App/StartupProbe.cs). Both halves are asserted — a clean exit code
# alone would also be produced by an app that quit before drawing anything.
#
# Usage:
#   scripts/release/smoke-launch.sh <label> <executable> [args...]
#
# Env:
#   PGNIMBUS_SMOKE_TIMEOUT  seconds to wait for the window (default 120)

set -euo pipefail

if [ $# -lt 2 ]; then
    echo "usage: $0 <label> <executable> [args...]" >&2
    exit 2
fi

label=$1
shift

if [ ! -x "$1" ]; then
    echo "FAILED ($label): $1 is not an executable file" >&2
    exit 1
fi

timeout_seconds=${PGNIMBUS_SMOKE_TIMEOUT:-120}
log=$(mktemp)
trap 'rm -f "$log"' EXIT

# No display on a CI runner, and the app is a GUI app. macOS runners have a
# real window server; Linux ones need Xvfb, same as the benchmark pipeline.
launcher=()
if [ "$(uname -s)" = "Linux" ] && [ -z "${DISPLAY:-}" ]; then
    if ! command -v xvfb-run >/dev/null 2>&1; then
        echo "xvfb-run not found and DISPLAY is unset — install xvfb before smoking a GUI build." >&2
        exit 1
    fi

    launcher=(xvfb-run -a --server-args="-screen 0 1280x800x24")
fi

echo "== smoke: $label"
echo "   $*"

# The app has no natural exit outside the probe, so a hang is a real failure
# mode and needs its own deadline. `timeout` is GNU-only and macOS runners do
# not ship it, hence the hand-rolled wait.
#
# The empty/non-empty branch (rather than a bare "${launcher[@]}") is
# deliberate: macOS runners' system bash is 3.2, which treats expanding an
# empty-but-declared array under `set -u` as an unbound variable.
if [ ${#launcher[@]} -eq 0 ]; then
    PGNIMBUS_STARTUP_PROBE=1 "$@" >"$log" 2>&1 &
else
    PGNIMBUS_STARTUP_PROBE=1 "${launcher[@]}" "$@" >"$log" 2>&1 &
fi
pid=$!

deadline=$(( $(date +%s) + timeout_seconds ))
while kill -0 "$pid" 2>/dev/null; do
    if [ "$(date +%s)" -ge "$deadline" ]; then
        kill -9 "$pid" 2>/dev/null || true
        echo "FAILED ($label): no window after ${timeout_seconds}s" >&2
        sed 's/^/   | /' "$log" >&2
        exit 1
    fi

    sleep 1
done

set +e
wait "$pid"
status=$?
set -e

sed 's/^/   | /' "$log"

if [ "$status" -ne 0 ]; then
    echo "FAILED ($label): exited with status $status" >&2
    exit 1
fi

if ! grep -q "PGNIMBUS_STARTUP_PROBE" "$log"; then
    echo "FAILED ($label): exited cleanly but never rendered a window" >&2
    exit 1
fi

echo "   ok: $(grep -o 'PGNIMBUS_STARTUP_PROBE.*' "$log" | head -1)"
