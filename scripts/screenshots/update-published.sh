#!/usr/bin/env bash
#
# Regenerates the screenshots that face users: the README, the documentation
# site (docs/screenshots) and the Microsoft Store listing
# (design/store/screenshots). The mapping from scenario to published file lives
# in tools/Screenshot/Marketing.cs.
#
# Run this before cutting a release, so the shots on the README and in the Store
# listing show the version being released rather than whichever one somebody
# last captured by hand.
#
# The animated GIFs in the README are not covered — they show motion and are
# still recorded by hand (see the screen-recording notes in the repo).
#
# On Linux this renders directly; anywhere else it goes through Docker, so the
# published images match what CI produces.

set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)

staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT

if [ "$(uname -s)" = "Linux" ] && [ -z "${PGNIMBUS_FORCE_DOCKER:-}" ]; then
    dotnet run --project "$repo_root/tools/Screenshot" -c Release -- "$staging" --publish "$repo_root"
else
    echo "Not on Linux — rendering in a container so the output matches CI."
    # The container only ever sees the repo read-only, so it publishes into a
    # tree of repo-relative paths under the output mount and the host copies
    # that over the working tree.
    "$repo_root/scripts/screenshots/render-linux.sh" "$staging" --publish /out/published
    cp -R "$staging/published/." "$repo_root/"
fi

echo
echo "Review them with: git status --short docs/screenshots design/store/screenshots"
