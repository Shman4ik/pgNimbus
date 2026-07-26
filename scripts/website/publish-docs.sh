#!/usr/bin/env bash
# Builds the MkDocs documentation site and publishes it to gh-pages under /docs/.
#
# The gh-pages branch hosts three independent things at three paths:
#   /            the hand-written landing page (scripts/website/publish-site.sh)
#   /docs/       this site
#   /dev/bench/  benchmark history (written by benchmark-action from release.yml)
# This script replaces /docs/ only, and must never touch the other two.
#
# Usage:
#   scripts/website/publish-docs.sh                          # build, commit, push
#   PGNIMBUS_DOCS_DRY_RUN=1 scripts/website/publish-docs.sh   # build + stage only
set -euo pipefail

repo_root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
cd "$repo_root"

# --strict turns a broken internal link or a page missing from the nav into a
# build failure, so a typo can't ship as a 404.
mkdocs build --strict

worktree="$(mktemp -d)"
trap 'git worktree remove --force "$worktree" 2>/dev/null || rm -rf "$worktree"' EXIT

git fetch origin gh-pages
git worktree add "$worktree" origin/gh-pages

rm -rf "$worktree/docs"
mkdir -p "$worktree/docs"
cp -R site/. "$worktree/docs/"

if [[ "${PGNIMBUS_DOCS_DRY_RUN:-0}" == "1" ]]; then
  echo "Staged docs in $worktree/docs (dry run, not committing):"
  ls -la "$worktree/docs"
  trap - EXIT   # keep the worktree around for inspection
  exit 0
fi

git -C "$worktree" add -A
if git -C "$worktree" diff --cached --quiet; then
  echo "Docs are already up to date on gh-pages."
  exit 0
fi

git -C "$worktree" commit -m "Update documentation site (from docs/ @ $(git rev-parse --short HEAD))"
git -C "$worktree" push origin HEAD:gh-pages
echo "Published to https://shman4ik.github.io/pgNimbus/docs/"
