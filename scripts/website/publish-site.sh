#!/usr/bin/env bash
# Publishes the project landing page (website/index.html + assets pulled from
# docs/screenshots and design/masters) to the root of the gh-pages branch.
#
# gh-pages also hosts the benchmark history under dev/bench/ (written by
# benchmark-action from the release pipeline) — this script never touches
# that directory, it only replaces index.html and assets/ at the root.
#
# Usage:
#   scripts/website/publish-site.sh            # stage, commit, and push
#   PGNIMBUS_SITE_DRY_RUN=1 scripts/website/publish-site.sh   # stage only, print the dir
set -euo pipefail

repo_root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
cd "$repo_root"

assets=(
  design/masters/logo/wordmark-light.png
  design/masters/logo/wordmark-dark.png
  design/masters/logo/social-preview.png
  design/masters/icon/icon-256.png
  docs/screenshots/main-light.png
  docs/screenshots/main-dark.png
  docs/screenshots/cold-start.gif
  docs/screenshots/completion-demo.gif
  docs/screenshots/explain-visualization.png
  docs/screenshots/command-palette.png
  docs/screenshots/server-activity.png
  docs/screenshots/connection-dialog.png
)

worktree="$(mktemp -d)"
trap 'git worktree remove --force "$worktree" 2>/dev/null || rm -rf "$worktree"' EXIT

git fetch origin gh-pages
git worktree add "$worktree" origin/gh-pages

rm -rf "$worktree/assets"
mkdir -p "$worktree/assets"
cp website/index.html "$worktree/index.html"
for a in "${assets[@]}"; do
  cp "$a" "$worktree/assets/$(basename "$a")"
done

if [[ "${PGNIMBUS_SITE_DRY_RUN:-0}" == "1" ]]; then
  echo "Staged site in $worktree (dry run, not committing):"
  ls -la "$worktree" "$worktree/assets"
  trap - EXIT   # keep the worktree around for inspection
  exit 0
fi

git -C "$worktree" add -A
if git -C "$worktree" diff --cached --quiet; then
  echo "Site is already up to date on gh-pages."
  exit 0
fi
git -C "$worktree" commit -m "Update project landing page (from website/index.html @ $(git rev-parse --short HEAD))"
git -C "$worktree" push origin HEAD:gh-pages
echo "Published to https://shman4ik.github.io/pgNimbus/"
