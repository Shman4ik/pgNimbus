#!/usr/bin/env bash
# Packages a `dotnet publish` NativeAOT output into an unsigned PgNimbus.app
# bundle and wraps it in a drag-to-Applications .dmg. macOS-only (sips /
# iconutil / hdiutil are stock tools on GitHub's macos-* runners).
#
# Usage: build-app-bundle.sh <publish-dir> <version> <rid> <out-dir>
#   publish-dir  output of `dotnet publish -r <rid> ...`
#   version      e.g. 1.2.3 (no leading v)
#   rid          osx-x64 | osx-arm64
#   out-dir      where to write the .dmg
set -euo pipefail

PUBLISH_DIR="$1"
VERSION="$2"
RID="$3"
OUT_DIR="$4"

case "$RID" in
  osx-x64) ARCH_LABEL="x64" ;;
  osx-arm64) ARCH_LABEL="arm64" ;;
  *) echo "Unknown RID: $RID (expected osx-x64 or osx-arm64)" >&2; exit 1 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK_DIR="$(mktemp -d)"
APP_DIR="$WORK_DIR/pgNimbus.app"

mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

# Info.plist with the version stamped in.
sed "s/__VERSION__/$VERSION/g" "$REPO_ROOT/installer/macos/Info.plist.template" \
  > "$APP_DIR/Contents/Info.plist"

# App icon: build a .iconset from the prepared square tile masters and compile
# it to .icns. Each iconset slot is filled from the hand-drawn master at that
# exact pixel size when one exists (design/masters/icon/icon-<px>.png), else
# high-quality-downscaled from the 1024 master — so the small sizes stay crisp
# instead of being resampled from a single mid-size PNG. Apple applies its own
# rounded-rect mask, so the masters are square full-bleed (no pre-rounding).
ICONSET_DIR="$WORK_DIR/app.iconset"
mkdir -p "$ICONSET_DIR"
MASTER_DIR="$REPO_ROOT/design/masters/icon"
ICON_1024="$MASTER_DIR/icon-1024.png"

# emit one iconset PNG of the given pixel size (uses the exact master if present)
emit_icon() {  # <pixels> <dest-filename>
  local px="$1" dest="$2" exact="$MASTER_DIR/icon-$1.png"
  if [ -f "$exact" ]; then
    cp "$exact" "$ICONSET_DIR/$dest"
  else
    sips -z "$px" "$px" "$ICON_1024" --out "$ICONSET_DIR/$dest" >/dev/null
  fi
}
for size in 16 32 64 128 256 512; do
  emit_icon "$size" "icon_${size}x${size}.png"
  emit_icon "$((size * 2))" "icon_${size}x${size}@2x.png"
done
iconutil -c icns "$ICONSET_DIR" -o "$APP_DIR/Contents/Resources/app.icns"

# Publish output -> Contents/MacOS.
cp -R "$PUBLISH_DIR/." "$APP_DIR/Contents/MacOS/"
chmod +x "$APP_DIR/Contents/MacOS/PgNimbus.App"

mkdir -p "$OUT_DIR"
DMG_NAME="pgNimbus-$VERSION-macos-$ARCH_LABEL.dmg"
hdiutil create -volname "pgNimbus $VERSION" \
  -srcfolder "$APP_DIR" \
  -ov -format UDZO \
  "$OUT_DIR/$DMG_NAME"

echo "Built $OUT_DIR/$DMG_NAME"
