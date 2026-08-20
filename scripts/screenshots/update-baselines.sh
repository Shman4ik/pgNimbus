#!/usr/bin/env bash
#
# Refreshes the committed visual-regression baselines in tools/Screenshot/baselines.
#
# Run this when a UI change is intended and CI reports the screenshots as
# CHANGED. Review the resulting diff in the PR the same way you would review
# code — a baseline update is the moment somebody signs off on how the app now
# looks, and it is the only thing standing between an accidental layout break
# and a release.
#
# On Linux this renders directly; anywhere else it goes through Docker, because
# baselines are pixel data and only comparable against the OS that made them
# (CI renders on ubuntu-latest).

set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
baseline_dir="$repo_root/tools/Screenshot/baselines"

staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT

if [ "$(uname -s)" = "Linux" ] && [ -z "${PGNIMBUS_FORCE_DOCKER:-}" ]; then
    dotnet run --project "$repo_root/tools/Screenshot" -c Release -- "$staging"
else
    echo "Not on Linux — rendering baselines in a container so they match CI."
    "$repo_root/scripts/screenshots/render-linux.sh" "$staging"
fi

rendered=$(find "$staging" -name '*.png' -not -name '*.diff.png' | wc -l)
if [ "$rendered" -eq 0 ]; then
    echo "No screenshots were rendered — refusing to wipe the baselines." >&2
    exit 1
fi

# Replace wholesale rather than overlay: a scenario that was deleted must lose
# its baseline too, otherwise the set quietly accumulates images of screens the
# app no longer has.
rm -rf "$baseline_dir"
mkdir -p "$baseline_dir"
find "$staging" -name '*.png' -not -name '*.diff.png' -exec cp {} "$baseline_dir/" \;

echo "Updated $rendered baselines in $baseline_dir"
echo "Review them with: git status --short tools/Screenshot/baselines"
