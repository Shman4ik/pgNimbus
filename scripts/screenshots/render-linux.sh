#!/usr/bin/env bash
#
# Renders the headless screenshot harness inside a Linux container.
#
# The visual-regression baselines are pixel data, and pixel data is only
# comparable against the OS that produced it. CI renders on ubuntu-latest, so
# baselines have to be rendered on Linux too — which a developer on Windows or
# macOS cannot do directly. This wraps the harness in the .NET SDK image so any
# machine with Docker produces frames CI will agree with.
#
# Usage:
#   scripts/screenshots/render-linux.sh <output-dir> [harness args...]
#
# Examples:
#   scripts/screenshots/render-linux.sh /tmp/shots
#   scripts/screenshots/render-linux.sh /tmp/shots main-window
#
# To refresh the committed baselines, prefer scripts/screenshots/update-baselines.sh.

set -euo pipefail

if [ $# -lt 1 ]; then
    echo "usage: $0 <output-dir> [harness args...]" >&2
    exit 2
fi

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
out_dir=$1
shift

mkdir -p "$out_dir"
out_dir=$(cd "$out_dir" && pwd)

image=${PGNIMBUS_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}

# Git Bash rewrites arguments that look like Unix paths before handing them to
# docker.exe, which turns "/src" into a Windows path and the mount into nonsense.
export MSYS_NO_PATHCONV=1

# ...but with that off, the host side of a bind mount has to be a real Windows
# path. A Git Bash "/x/source/..." is neither, and Docker Desktop silently
# treats it as an in-VM path: the run succeeds and the host directory stays
# empty. cygpath exists only on Windows, so this is a no-op everywhere else.
if command -v cygpath >/dev/null 2>&1; then
    repo_root=$(cygpath -w "$repo_root")
    out_dir=$(cygpath -w "$out_dir")
fi

# The repo goes in read-only and is copied to a container-local tree: bin/ and
# obj/ from the host are a different OS's build output, and letting MSBuild find
# them produces confusing failures rather than a clean Linux build.
docker run --rm \
    -v "$repo_root:/src:ro" \
    -v "$out_dir:/out" \
    -e DOTNET_NOLOGO=1 \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    "$image" \
    bash -c '
        set -euo pipefail

        # Skia is bundled, but it links against the system fontconfig, which the
        # SDK image does not ship. GitHub'"'"'s ubuntu runners already have it, so
        # this is the one thing the container needs that CI does not.
        apt-get update -qq
        apt-get install -y -qq --no-install-recommends libfontconfig1 >/dev/null

        mkdir -p /work
        tar -C /src -cf - \
            --exclude=bin --exclude=obj --exclude=.git \
            --exclude=site --exclude=TestResults . | tar -C /work -xf -
        cd /work
        dotnet run --project tools/Screenshot -c Release -- /out "$@"
    ' bash "$@"
