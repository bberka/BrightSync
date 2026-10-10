#!/usr/bin/env bash
# Publishes BrightSync for one Linux runtime identifier and builds every package format for it.
#
#   packaging/build-linux.sh <rid> <version> <output-dir>
#
#   rid      linux-x64 | linux-arm64 | linux-arm | linux-musl-x64 | linux-musl-arm64 | linux-musl-arm
#            linux-portable: framework-dependent build for any architecture with a .NET 10 runtime
#            (riscv64, loongarch64, x86, ...). Needs `dotnet` on PATH and ships only the tar.gz.
#   version  release version, e.g. 0.19.0
#
# Environment:
#   BS_AOT=auto|true|false   Native AOT. auto = on when the build host matches the target architecture,
#                            the target is x64 or arm64, and the libc is glibc (default). arm, musl
#                            and cross builds use a self-contained single file instead.
#   NFPM=nfpm                nfpm binary used for deb, rpm, apk and archlinux packages
#   APPIMAGETOOL=            path of appimagetool; AppImage is skipped when unset
#
# Outputs (naming contract in docs/PACKAGING.md), all written to <output-dir>:
#   BrightSync-<version>-<rid>.tar.gz        portable archive with install.sh
#   BrightSync-<version>-<rid>.deb|.rpm|.pkg.tar.zst   glibc targets
#   BrightSync-<version>-<rid>.apk                      musl targets
#   BrightSync-<version>-<rid>.AppImage                 glibc targets, when APPIMAGETOOL is set
set -euo pipefail

rid=${1:?usage: build-linux.sh <rid> <version> <output-dir>}
version=${2:?usage: build-linux.sh <rid> <version> <output-dir>}
out=${3:?usage: build-linux.sh <rid> <version> <output-dir>}

root=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$out"
out=$(cd "$out" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

portable=false
case "$rid" in
    linux-portable) portable=true; arch=any; nfpm_arch=""; appimage_arch="" ;;
    linux-x64|linux-musl-x64) arch=x64; nfpm_arch=amd64; appimage_arch=x86_64 ;;
    linux-arm64|linux-musl-arm64) arch=arm64; nfpm_arch=arm64; appimage_arch=aarch64 ;;
    linux-arm|linux-musl-arm) arch=arm; nfpm_arch=arm7; appimage_arch=armhf ;;
    *) echo "Unsupported runtime identifier: $rid" >&2; exit 2 ;;
esac
case "$rid" in linux-musl-*) musl=true ;; *) musl=false ;; esac

host_arch=$(uname -m)
case "$host_arch" in x86_64) host=x64 ;; aarch64|arm64) host=arm64 ;; armv7l|armv8l) host=arm ;; *) host=$host_arch ;; esac

publish="$work/publish"
if [ "$portable" = true ]; then
    echo "==> Publishing portable framework-dependent build $version"
    dotnet publish "$root/src/BrightSync.csproj" \
        -c Release \
        -p:BrightSyncTargetOs=linux -p:BrightSyncPortable=true \
        -p:PublishAot=false -p:UseAppHost=false \
        -p:DebugType=None -p:DebugSymbols=false \
        -p:Version="$version" -p:AssemblyVersion="$version" -p:FileVersion="$version" \
        -p:InformationalVersion="$version" \
        -o "$publish"
    rm -f "$publish"/*.pdb
    # Launcher named like the self-contained binary so install.sh and the desktop file work unchanged.
    cat >"$publish/BrightSync" <<'EOF'
#!/bin/sh
here=$(dirname "$(readlink -f "$0")")
exec dotnet "$here/BrightSync.dll" "$@"
EOF
else
    aot=${BS_AOT:-auto}
    if [ "$aot" = auto ]; then
        if [ "$arch" = "$host" ] && [ "$arch" != arm ] && [ "$musl" = false ]; then aot=true; else aot=false; fi
    fi

    echo "==> Publishing $rid $version (AOT=$aot)"
    dotnet publish "$root/src/BrightSync.csproj" \
        -c Release -r "$rid" \
        -p:PublishAot="$aot" \
        -p:SelfContained=true \
        -p:PublishSingleFile=true \
        -p:DebugType=None -p:DebugSymbols=false \
        -p:Version="$version" -p:AssemblyVersion="$version" -p:FileVersion="$version" \
        -p:InformationalVersion="$version" \
        -o "$publish"
    rm -f "$publish"/*.pdb
fi
test -f "$publish/BrightSync" || { echo "Publish did not produce BrightSync" >&2; exit 1; }
chmod 0755 "$publish/BrightSync"

base="BrightSync-$version-$rid"

echo "==> Portable archive"
stage="$work/$base"
mkdir -p "$stage/app"
cp -a "$publish/." "$stage/app/"
cp "$root/packaging/linux/install.sh" "$root/packaging/linux/brightsync.desktop" \
    "$root/packaging/linux/99-brightsync-i2c.rules" "$root/packaging/linux/brightsync-i2c.conf" "$stage/"
cp "$root/src/Resources/app.png" "$stage/brightsync.png"
cp "$root/LICENSE" "$stage/"
cat >"$stage/README.txt" <<EOF
BrightSync $version ($rid)

Install for the current user:   ./install.sh
Install system-wide:            sudo ./install.sh --system
Run without installing:         ./app/BrightSync

External monitors need DDC/CI access: sudo ./install.sh --setup-i2c
Documentation: https://github.com/bberka/BrightSync/blob/main/docs/LINUX.md
EOF
chmod 0755 "$stage/install.sh"
tar -C "$work" --owner=0 --group=0 -czf "$out/$base.tar.gz" "$base"

if [ "$portable" = true ]; then
    echo "Portable build: archive only"
elif command -v "${NFPM:-nfpm}" >/dev/null 2>&1; then
    echo "==> Native packages"
    export NFPM_ARCH=$nfpm_arch NFPM_VERSION=$version BS_PUBLISH=$publish
    cd "$root"
    if [ "$musl" = true ]; then
        "${NFPM:-nfpm}" package -f packaging/linux/nfpm.yaml -p apk -t "$out/$base.apk"
    else
        "${NFPM:-nfpm}" package -f packaging/linux/nfpm.yaml -p deb -t "$out/$base.deb"
        "${NFPM:-nfpm}" package -f packaging/linux/nfpm.yaml -p rpm -t "$out/$base.rpm"
        "${NFPM:-nfpm}" package -f packaging/linux/nfpm.yaml -p archlinux -t "$out/$base.pkg.tar.zst"
    fi
else
    echo "nfpm not found; skipping deb/rpm/apk/archlinux packages" >&2
fi

if [ "$portable" = false ] && [ "$musl" = false ] && [ -n "${APPIMAGETOOL:-}" ]; then
    echo "==> AppImage"
    appdir="$work/BrightSync.AppDir"
    mkdir -p "$appdir/usr/bin" "$appdir/usr/lib/brightsync"
    cp -a "$publish/." "$appdir/usr/lib/brightsync/"
    cat >"$appdir/AppRun" <<'EOF'
#!/bin/sh
here=$(dirname "$(readlink -f "$0")")
exec "$here/usr/lib/brightsync/BrightSync" "$@"
EOF
    chmod 0755 "$appdir/AppRun"
    cp "$root/packaging/linux/brightsync.desktop" "$appdir/brightsync.desktop"
    cp "$root/src/Resources/app.png" "$appdir/brightsync.png"
    ARCH=$appimage_arch "$APPIMAGETOOL" --appimage-extract-and-run "$appdir" "$out/$base.AppImage" 2>&1 \
        || ARCH=$appimage_arch "$APPIMAGETOOL" "$appdir" "$out/$base.AppImage"
else
    echo "Skipping AppImage (portable or musl target, or APPIMAGETOOL unset)"
fi

echo "==> Done"
ls -la "$out" | grep "$base" || true
