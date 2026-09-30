#!/usr/bin/env bash
# Packages a `dotnet publish` NativeAOT output into the three Linux release
# artifacts: a plain .tar.gz, an .AppImage, and a .deb. Linux-only (dpkg-deb
# ships with any Debian-family distro/runner; a pinned, checksum-verified
# appimagetool and AppImage runtime are downloaded on demand, and appimagetool
# runs with --appimage-extract-and-run since CI runners lack FUSE).
#
# Usage: build-packages.sh <publish-dir> <version> <rid> <out-dir>
#   publish-dir  output of `dotnet publish -r <rid> ...`
#   version      e.g. 1.2.3 (no leading v)
#   rid          linux-x64 | linux-arm64
#   out-dir      where to write the packages
set -euo pipefail

PUBLISH_DIR="$1"
VERSION="$2"
RID="$3"
OUT_DIR="$4"

case "$RID" in
  linux-x64)   ARCH_LABEL="x64";   DEB_ARCH="amd64"; APPIMAGE_ARCH="x86_64"  ;;
  linux-arm64) ARCH_LABEL="arm64"; DEB_ARCH="arm64"; APPIMAGE_ARCH="aarch64" ;;
  *) echo "Unknown RID: $RID (expected linux-x64 or linux-arm64)" >&2; exit 1 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"

BASE_NAME="pgNimbus-$VERSION-linux-$ARCH_LABEL"
MASTER_DIR="$REPO_ROOT/design/masters/icon"
DESKTOP_TEMPLATE="$REPO_ROOT/packaging/linux/pgnimbus.desktop.template"

# Stage what actually ships: the publish output minus the *.dbg side file
# NativeAOT strips debug symbols into (the benchmark script excludes it from
# "publish size" for the same reason).
STAGE_DIR="$WORK_DIR/stage"
mkdir -p "$STAGE_DIR"
cp -R "$PUBLISH_DIR/." "$STAGE_DIR/"
rm -f "$STAGE_DIR"/*.dbg
chmod +x "$STAGE_DIR/PgNimbus.App"

# The desktop entry is shared, but Exec differs per format: inside an AppImage
# the binary is invoked by its in-bundle name, the deb exposes a
# /usr/bin/pgnimbus symlink.
emit_desktop() { # <exec-line> <dest>
  sed "s|__EXEC__|$1|" "$DESKTOP_TEMPLATE" > "$2"
}

# ---- tar.gz -----------------------------------------------------------------
TAR_ROOT="$WORK_DIR/$BASE_NAME"
mkdir "$TAR_ROOT"
cp -R "$STAGE_DIR/." "$TAR_ROOT/"
tar -C "$WORK_DIR" -czf "$OUT_DIR/$BASE_NAME.tar.gz" "$BASE_NAME"
echo "Built $OUT_DIR/$BASE_NAME.tar.gz"

# ---- AppImage ---------------------------------------------------------------
APPDIR="$WORK_DIR/AppDir"
mkdir -p "$APPDIR/usr/bin"
cp -R "$STAGE_DIR/." "$APPDIR/usr/bin/"
emit_desktop "PgNimbus.App" "$APPDIR/pgnimbus.desktop"
# The 256px master is the circular navy badge with transparent corners —
# right for Linux desktop icons, which draw over arbitrary backgrounds.
cp "$MASTER_DIR/icon-256.png" "$APPDIR/pgnimbus.png"
cp "$MASTER_DIR/icon-256.png" "$APPDIR/.DirIcon"
# AppRun as a symlink: NativeAOT resolves its side-car .so files
# (libSkiaSharp, libHarfBuzzSharp) relative to /proc/self/exe, so no
# wrapper script or LD_LIBRARY_PATH is needed.
ln -s usr/bin/PgNimbus.App "$APPDIR/AppRun"

# Pinned, checksum-verified tooling (security audit 2026-09, finding 5).
# appimagetool used to come from the moving "continuous" release with no
# checksum, and it then fetched the newest type2 runtime on its own — and that
# runtime is the first code that runs when a user starts the AppImage, so it
# ships inside every Linux release. Both are now fixed releases, and the
# runtime is handed over with --runtime-file so appimagetool never downloads
# anything. The sha256 values are the `digest` GitHub publishes for each
# release asset, read with
#   gh api repos/AppImage/appimagetool/releases/tags/1.9.1 --jq '.assets[] | .name + " " + .digest'
#   gh api repos/AppImage/type2-runtime/releases/tags/20251108 --jq '.assets[] | .name + " " + .digest'
# To move to a newer release, bump the version and every digest together.
APPIMAGETOOL_VERSION="1.9.1"
TYPE2_RUNTIME_VERSION="20251108"
case "$APPIMAGE_ARCH" in
  x86_64)
    APPIMAGETOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
    RUNTIME_SHA256="2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"
    ;;
  aarch64)
    APPIMAGETOOL_SHA256="f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158"
    RUNTIME_SHA256="00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444"
    ;;
esac

fetch_verified() { # <url> <sha256> <dest>
  curl -fsSL --retry 3 -o "$3" "$1"
  echo "$2  $3" | sha256sum -c --quiet - || {
    echo "sha256 mismatch for $1 (expected $2); refusing to use it" >&2
    exit 1
  }
}

APPIMAGETOOL="$WORK_DIR/appimagetool"
fetch_verified \
  "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-$APPIMAGE_ARCH.AppImage" \
  "$APPIMAGETOOL_SHA256" "$APPIMAGETOOL"
chmod +x "$APPIMAGETOOL"

RUNTIME="$WORK_DIR/runtime-$APPIMAGE_ARCH"
fetch_verified \
  "https://github.com/AppImage/type2-runtime/releases/download/$TYPE2_RUNTIME_VERSION/runtime-$APPIMAGE_ARCH" \
  "$RUNTIME_SHA256" "$RUNTIME"

# --appimage-extract-and-run: CI runners have no FUSE, so appimagetool (itself
# an AppImage) has to unpack itself to run.
ARCH="$APPIMAGE_ARCH" "$APPIMAGETOOL" --appimage-extract-and-run \
  --runtime-file "$RUNTIME" \
  "$APPDIR" "$OUT_DIR/$BASE_NAME.AppImage"
echo "Built $OUT_DIR/$BASE_NAME.AppImage"

# ---- .deb -------------------------------------------------------------------
# Debian versions use ~ for prerelease (sorts before the release), where
# semver uses -. Affects workflow_dispatch test versions like 0.0.0-ci.42.
DEB_VERSION="${VERSION/-/~}"
DEB_ROOT="$WORK_DIR/deb"
mkdir -p "$DEB_ROOT/DEBIAN" \
         "$DEB_ROOT/usr/lib/pgnimbus" \
         "$DEB_ROOT/usr/bin" \
         "$DEB_ROOT/usr/share/applications"
cp -R "$STAGE_DIR/." "$DEB_ROOT/usr/lib/pgnimbus/"
ln -s ../lib/pgnimbus/PgNimbus.App "$DEB_ROOT/usr/bin/pgnimbus"
emit_desktop "pgnimbus" "$DEB_ROOT/usr/share/applications/pgnimbus.desktop"
for px in 16 24 32 48 256; do
  dest="$DEB_ROOT/usr/share/icons/hicolor/${px}x${px}/apps"
  mkdir -p "$dest"
  cp "$MASTER_DIR/icon-$px.png" "$dest/pgnimbus.png"
done

INSTALLED_SIZE_KB=$(du -sk "$DEB_ROOT/usr" | cut -f1)
# Depends: the X11-family libs Avalonia's X11 backend touches at runtime
# (core X11, session management, XInput2, XRandR, XCursor, XExt, Xinerama)
# plus fontconfig for Skia's font lookup — so the package runs on minimal
# installs too. Skia and HarfBuzz themselves are bundled as side-car .so
# files, not system deps.
cat > "$DEB_ROOT/DEBIAN/control" <<EOF
Package: pgnimbus
Version: $DEB_VERSION
Section: database
Priority: optional
Architecture: $DEB_ARCH
Installed-Size: $INSTALLED_SIZE_KB
Maintainer: Dmitrii Shmanev <shman4ik@gmail.com>
Homepage: https://github.com/Shman4ik/pgNimbus
Depends: libx11-6, libice6, libsm6, libfontconfig1, libfreetype6, libxext6, libxi6, libxcursor1, libxinerama1, libxrandr2, libsecret-1-0
Description: Fast, open-source PostgreSQL GUI client
 A PostgreSQL-first database client built with .NET and Avalonia, compiled
 to a NativeAOT binary for instant startup. Streams large result sets while
 they load. MIT licensed.
EOF
dpkg-deb --build --root-owner-group "$DEB_ROOT" "$OUT_DIR/$BASE_NAME.deb"
echo "Built $OUT_DIR/$BASE_NAME.deb"
