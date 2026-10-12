#!/usr/bin/env bash
# Fails when this app's copy of nimbusUi (shared/nimbusUi) differs from a nimbusUi checkout,
# normally nimbusUi's main (#343). Line endings are ignored: the three repositories store
# them differently, and the sync procedure in CLAUDE.md compares with `tr -d '\r' | md5sum`
# for the same reason. A file present on one side only is drift as well.
#
# Usage: scripts/check-nimbusui-drift.sh <nimbusUi checkout> [<subtree dir>]
#   The subtree defaults to shared/nimbusUi. Both sides are listed with `git ls-files`, so
#   build output (bin/, obj/) and other untracked files never count.
#
# The same script is in pgNimbus; keep the two copies identical.
set -euo pipefail

upstream=${1:?usage: check-nimbusui-drift.sh <nimbusUi checkout> [<subtree dir>]}
subtree=${2:-shared/nimbusUi}

list() { git -C "$1" ls-files | LC_ALL=C sort; }
digest() { tr -d '\r' < "$1" | md5sum | cut -d' ' -f1; }

upstream_files=$(list "$upstream")
subtree_files=$(list "$subtree")

drift=0
only_upstream=$(LC_ALL=C comm -23 <(printf '%s\n' "$upstream_files") <(printf '%s\n' "$subtree_files"))
only_subtree=$(LC_ALL=C comm -13 <(printf '%s\n' "$upstream_files") <(printf '%s\n' "$subtree_files"))
if [ -n "$only_upstream" ]; then
  printf 'Only in nimbusUi (missing from %s):\n%s\n' "$subtree" "$only_upstream" | sed 's/^/  /'
  drift=1
fi
if [ -n "$only_subtree" ]; then
  printf 'Only in %s (not in nimbusUi):\n%s\n' "$subtree" "$only_subtree" | sed 's/^/  /'
  drift=1
fi

while IFS= read -r file; do
  [ -n "$file" ] || continue
  if [ "$(digest "$upstream/$file")" != "$(digest "$subtree/$file")" ]; then
    echo "  differs: $file"
    drift=1
  fi
done < <(LC_ALL=C comm -12 <(printf '%s\n' "$upstream_files") <(printf '%s\n' "$subtree_files"))

if [ "$drift" -ne 0 ]; then
  echo "::error::$subtree has drifted from nimbusUi. Sync by copying files (CLAUDE.md, 'The sibling project'); see CONTRIBUTING.md for a 'Take nimbusUi' PR whose nimbusUi PR is still open."
  exit 1
fi

echo "$subtree matches nimbusUi ($(printf '%s\n' "$subtree_files" | wc -l | tr -d ' ') files, line endings ignored)."
