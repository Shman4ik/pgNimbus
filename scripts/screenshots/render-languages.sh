#!/usr/bin/env bash
#
# Renders every screenshot scenario in English and in every translation, light
# theme only, for a person to look at: <out-dir>/<lang>/<scenario>.png.
#
# The languages are "en" plus one per src/PgNimbus.Core/Localization/Strings.<tag>.resx,
# read from the file (so a translation that is not built in yet renders too).
# PGNIMBUS_RENDER_LANGUAGES=de,ru,qps-ploc renders those instead ("en" is English,
# "qps-ploc" the pseudo-locale). The l10n-review workflow runs this and posts the
# images on the pull request (.claude/rules/headless-tests.md).
#
# Renders on the host, like update-published.sh: glyphs differ slightly between
# operating systems, but this is for reading, not for comparing with baselines.
#
# Usage:
#   scripts/screenshots/render-languages.sh <out-dir> [scenario-substring]
#
# Examples:
#   scripts/screenshots/render-languages.sh /tmp/l10n
#   PGNIMBUS_RENDER_LANGUAGES=qps-ploc scripts/screenshots/render-languages.sh /tmp/l10n connection
#
# PGNIMBUS_RENDER_NO_BUILD=1 skips the build (CI has built Release already).

set -euo pipefail

if [ $# -lt 1 ]; then
    echo "usage: $0 <out-dir> [scenario-substring]" >&2
    exit 2
fi

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
out_dir=$1
filter=${2:-}
mkdir -p "$out_dir"
out_dir=$(cd "$out_dir" && pwd)

localization="$repo_root/src/PgNimbus.Core/Localization"

if [ -n "${PGNIMBUS_RENDER_LANGUAGES:-}" ]; then
    IFS=',' read -r -a languages <<< "$PGNIMBUS_RENDER_LANGUAGES"
else
    languages=(en)
    for file in "$localization"/Strings.*.resx; do
        [ -e "$file" ] || continue
        tag=$(basename "$file" .resx)
        languages+=("${tag#Strings.}")
    done
fi

cd "$repo_root"

if [ "${PGNIMBUS_RENDER_NO_BUILD:-}" != "1" ]; then
    dotnet build tools/Screenshot -c Release -v quiet -nologo
fi

for lang in "${languages[@]}"; do
    lang=$(echo "$lang" | tr -d '[:space:]')
    [ -n "$lang" ] || continue

    args=()
    if [ "$lang" != "en" ]; then
        if [ -f "$localization/Strings.$lang.resx" ]; then
            args=(--language "$localization/Strings.$lang.resx")
        else
            # A built-in tag: the pseudo-locale, or a language the build carries.
            args=(--language "$lang")
        fi
    fi

    dest="$out_dir/$lang"
    rm -rf "$dest"
    mkdir -p "$dest"
    echo "== $lang"

    positional=("$dest")
    if [ -n "$filter" ]; then
        positional+=("$filter")
    fi

    dotnet run --project tools/Screenshot -c Release --no-build -- \
        "${positional[@]}" --theme light ${args[@]+"${args[@]}"} > "$dest.log" 2>&1 || {
        cat "$dest.log" >&2
        echo "render failed for $lang" >&2
        exit 1
    }

    # <scenario>.light.png -> <scenario>.png: one theme, so the suffix says nothing.
    for png in "$dest"/*.light.png; do
        [ -e "$png" ] || continue
        mv "$png" "${png%.light.png}.png"
    done

    echo "   $(find "$dest" -name '*.png' | wc -l | tr -d ' ') images in $dest"
    rm -f "$dest.log"
done
