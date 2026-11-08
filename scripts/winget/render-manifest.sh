#!/usr/bin/env bash
# Fills in the winget manifest templates in packaging/winget/ for one release
# and writes the three real manifest files to an output directory. This does
# NOT submit anything to microsoft/winget-pkgs — it just produces the files
# for manual (or, later, automated) submission.
#
# The installer manifest lists both Windows zips, pgNimbus-<version>-win-x64.zip
# and pgNimbus-<version>-win-arm64.zip, under <release-base-url>.
#
# Usage: render-manifest.sh <version> <release-base-url> <x64-zip-sha256> <arm64-zip-sha256> <out-dir>
#   e.g. render-manifest.sh 1.2.3 https://github.com/Shman4ik/pgNimbus/releases/download/v1.2.3 <sha> <sha> manifests
set -euo pipefail

if [ "$#" -ne 5 ]; then
  echo "usage: $0 <version> <release-base-url> <x64-zip-sha256> <arm64-zip-sha256> <out-dir>" >&2
  exit 2
fi

VERSION="$1"
RELEASE_BASE_URL="${2%/}"
X64_SHA256="$3"
ARM64_SHA256="$4"
OUT_DIR="$5"

for sha in "$X64_SHA256" "$ARM64_SHA256"; do
  [[ "$sha" =~ ^[0-9a-fA-F]{64}$ ]] || { echo "not a SHA256: '$sha'" >&2; exit 2; }
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TEMPLATE_DIR="$REPO_ROOT/packaging/winget"

mkdir -p "$OUT_DIR"

for template in "$TEMPLATE_DIR"/*.yaml.template; do
  name="$(basename "$template" .template)"
  sed \
    -e "s|__VERSION__|$VERSION|g" \
    -e "s|__RELEASE_BASE_URL__|$RELEASE_BASE_URL|g" \
    -e "s|__X64_SHA256__|$X64_SHA256|g" \
    -e "s|__ARM64_SHA256__|$ARM64_SHA256|g" \
    "$template" > "$OUT_DIR/$name"
done

# A placeholder left behind (a template that grew one this script doesn't know)
# would otherwise only show up as a winget validate error, or not at all.
if grep -n '__[A-Z0-9_]*__' "$OUT_DIR"/*.yaml; then
  echo "unfilled placeholders in the rendered manifests" >&2
  exit 1
fi

echo "Rendered winget manifests to $OUT_DIR"
