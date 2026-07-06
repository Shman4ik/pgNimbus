#!/usr/bin/env bash
# Fills in the winget manifest templates in packaging/winget/ for one release
# and writes the three real manifest files to an output directory. This does
# NOT submit anything to microsoft/winget-pkgs — it just produces the files
# for manual (or, later, automated) submission.
#
# Usage: render-manifest.sh <version> <msi-sha256> <release-url> <out-dir>
set -euo pipefail

VERSION="$1"
SHA256="$2"
RELEASE_URL="$3"
OUT_DIR="$4"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TEMPLATE_DIR="$REPO_ROOT/packaging/winget"

mkdir -p "$OUT_DIR"

for template in "$TEMPLATE_DIR"/*.yaml.template; do
  name="$(basename "$template" .template)"
  sed \
    -e "s|__VERSION__|$VERSION|g" \
    -e "s|__SHA256__|$SHA256|g" \
    -e "s|__RELEASE_URL__|$RELEASE_URL|g" \
    "$template" > "$OUT_DIR/$name"
done

echo "Rendered winget manifests to $OUT_DIR"
