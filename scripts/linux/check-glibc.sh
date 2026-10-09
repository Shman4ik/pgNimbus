#!/usr/bin/env bash
# Fails when any ELF file in a publish directory needs a newer glibc than the
# oldest system the Linux packages promise to run on.
#
# The binary records the version of every glibc symbol it was linked against,
# and the linker picks the newest version the *build machine's* glibc has. So a
# build on a newer runner silently raises the floor: 1.1.0, linked on
# ubuntu-24.04 (glibc 2.39), bound fmod and fmodf to GLIBC_2.38 and would not
# start on Ubuntu 22.04, Debian 12 or RHEL 9 at all ("version `GLIBC_2.38' not
# found"). The AppImage catalog tests on the oldest supported Ubuntu LTS and
# refused it for exactly that (AppImage/appimage.github.io#6624).
#
# Usage: check-glibc.sh <publish-dir> <max-glibc>
#   publish-dir  output of `dotnet publish -r linux-<arch> ...`
#   max-glibc    e.g. 2.35 (Ubuntu 22.04)
set -euo pipefail

DIR="$1"
MAX="$2"

highest() { # <file>: the newest GLIBC_x.y the file references, or nothing
  objdump -T "$1" | grep -oE 'GLIBC_[0-9]+(\.[0-9]+)+' | sed 's/^GLIBC_//' | sort -uV | tail -n 1
}

newer_than_max() { # <version>: true when version > MAX
  [ "$1" != "$MAX" ] && [ "$(printf '%s\n%s\n' "$1" "$MAX" | sort -V | tail -n 1)" = "$1" ]
}

failed=0
checked=0
while IFS= read -r -d '' file; do
  # Only ELF files: the publish output also holds .pdb side files.
  [ "$(head -c 4 "$file" | od -An -tx1 | tr -d ' \n')" = "7f454c46" ] || continue
  checked=$((checked + 1))
  version="$(highest "$file")"
  if [ -z "$version" ]; then
    echo "$(basename "$file"): no versioned glibc symbols"
  elif newer_than_max "$version"; then
    echo "$(basename "$file"): needs glibc $version, above the $MAX ceiling. Symbols:" >&2
    objdump -T "$file" | grep -E 'GLIBC_[0-9]' | awk '{print $(NF-1), $NF}' \
      | sed 's/[()]//g; s/^GLIBC_//' | while read -r v sym; do
          if newer_than_max "$v"; then echo "  $sym@GLIBC_$v" >&2; fi
        done
    failed=1
  else
    echo "$(basename "$file"): needs glibc $version (ceiling $MAX)"
  fi
# Not *.dbg: the NativeAOT symbols file is an ELF too, but no package ships it
# (build-packages.sh drops it), and the binutils of Ubuntu 22.04 cannot read
# its DWARF 5 ("invalid operation").
done < <(find "$DIR" -maxdepth 1 -type f ! -name '*.dbg' -print0)

if [ "$checked" -eq 0 ]; then
  echo "No ELF files found in $DIR" >&2
  exit 1
fi
if [ "$failed" -ne 0 ]; then
  echo "Build on an older system (the oldest supported Ubuntu LTS), or raise the ceiling on purpose." >&2
  exit 1
fi
