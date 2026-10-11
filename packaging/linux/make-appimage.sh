#!/usr/bin/env bash
# Builds an AppImage from a published BrightSync directory.
#
#   APPIMAGETOOL=/path/to/appimagetool packaging/linux/make-appimage.sh <publish-dir> <output.AppImage> <arch>
#
#   arch  x86_64 | aarch64 | armhf (the ARCH value appimagetool expects)
set -euo pipefail

publish=${1:?usage: make-appimage.sh <publish-dir> <output.AppImage> <arch>}
output=${2:?usage: make-appimage.sh <publish-dir> <output.AppImage> <arch>}
arch=${3:?usage: make-appimage.sh <publish-dir> <output.AppImage> <arch>}
tool=${APPIMAGETOOL:?set APPIMAGETOOL to the appimagetool binary}

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

appdir="$work/BrightSync.AppDir"
mkdir -p "$appdir/usr/lib/brightsync"
cp -a "$publish/." "$appdir/usr/lib/brightsync/"
# A publish directory that travelled through a non-POSIX filesystem or archive may have lost the bit.
chmod 0755 "$appdir/usr/lib/brightsync/BrightSync"
cat >"$appdir/AppRun" <<'RUN'
#!/bin/sh
here=$(dirname "$(readlink -f "$0")")
exec "$here/usr/lib/brightsync/BrightSync" "$@"
RUN
chmod 0755 "$appdir/AppRun"
cp "$here/brightsync.desktop" "$appdir/brightsync.desktop"
cp "$root/src/BrightSync.UI/Resources/app.png" "$appdir/brightsync.png"

# Extract-and-run keeps appimagetool working on machines and CI runners without FUSE.
ARCH=$arch APPIMAGE_EXTRACT_AND_RUN=1 "$tool" "$appdir" "$output"
chmod 0755 "$output"
