#!/usr/bin/env bash
#
# Regenerates the screenshots that face users: the README, the documentation
# site (docs/screenshots) and the Microsoft Store listing
# (design/store/screenshots). The mapping from scenario to published file lives
# in tools/Screenshot/Marketing.cs.
#
# Run this in any PR that changes what they show (CLAUDE.md UI rule 9) and
# before cutting a release, so the shots on the README and in the Store
# listing show the version being released rather than whichever one somebody
# last captured by hand.
#
# The animated GIFs in the README are not covered — they show motion and are
# still recorded by hand (see the screen-recording notes in the repo).
#
# Rendered on the host, not in the CI container, and meant to be run on
# Windows: the SQL editor, the plan and every other monospace pane ask for
# Cascadia Code or Consolas, which only Windows ships. The Linux container has
# neither, and the Store listing went out with its SQL set in a proportional
# font until 2026-09. The visual-regression baselines are a different matter
# and still come from the container (update-baselines.sh), because they have
# to match CI pixel for pixel; these only have to look right.

set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) ;;
    *) echo "Warning: not on Windows - monospace panes will fall back to another font." >&2 ;;
esac

staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT

dotnet run --project "$repo_root/tools/Screenshot" -c Release -- "$staging" --publish "$repo_root"

echo
echo "Review them with: git status --short docs/screenshots design/store/screenshots"
