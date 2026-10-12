#!/usr/bin/env bash
# Install or update BrightSync from the latest Linux release.
#   curl -fsSL https://raw.githubusercontent.com/bberka/BrightSync/master/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/bberka/BrightSync/master/install.sh | bash -s -- --system
set -euo pipefail

repo=bberka/BrightSync
tmp=''
cleanup() { [[ -z "$tmp" ]] || rm -rf -- "$tmp"; }
trap cleanup EXIT

fail() { printf 'BrightSync installer: %s\n' "$*" >&2; exit 1; }
command -v curl >/dev/null || fail 'curl is required.'
command -v tar >/dev/null || fail 'tar is required.'

case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  aarch64|arm64) arch=arm64 ;;
  armv7l|armv7*) arch=arm ;;
  *) fail "unsupported CPU architecture: $(uname -m). See https://github.com/$repo/releases/latest" ;;
esac

rid="linux-$arch"
if command -v ldd >/dev/null 2>&1; then
  ldd_version=$(ldd --version 2>&1 || true)
  case "$ldd_version" in *musl*) rid="linux-musl-$arch" ;; esac
fi

release_url=$(curl -fsSL -o /dev/null -w '%{url_effective}' "https://github.com/$repo/releases/latest") || fail 'could not find the latest release.'
tag=${release_url##*/}
version=${tag#v}
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "could not determine release version from $release_url"
archive="BrightSync-$version-$rid.tar.gz"
url="https://github.com/$repo/releases/download/$tag/$archive"
tmp=$(mktemp -d "${TMPDIR:-/tmp}/brightsync-install.XXXXXX")
curl -fL --retry 3 "$url" -o "$tmp/$archive" || fail "download failed: $url"
tar -xzf "$tmp/$archive" -C "$tmp"
folder="$tmp/BrightSync-$version-$rid"
[[ -x "$folder/install.sh" ]] || fail 'release archive did not contain its installer.'

# Forward options such as --system, --setup-i2c, and --uninstall to the archive installer.
bash "$folder/install.sh" "$@"
