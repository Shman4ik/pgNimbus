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

# Rendered (the smoke check still runs them) but never given a baseline:
# their frames differ from one render to the next, so a baseline only
# produces false CHANGED reports. The security window's segmented tab strip
# animates the selected tab, and the harness catches it at a different
# moment each time (0.3-0.4 % on CI; commit 3b222bb). A wholesale refresh
# put them back once (#261) and main went red; this keeps them out until
# the render is made deterministic.
for unstable in security-window security-window-permissions \
                security-window-default-privileges security-window-rls; do
    rm -f "$baseline_dir/$unstable.light.png" "$baseline_dir/$unstable.dark.png"
done

echo "Updated $rendered baselines in $baseline_dir"
echo "Review them with: git status --short tools/Screenshot/baselines"
