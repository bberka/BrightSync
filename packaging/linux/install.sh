#!/bin/sh
# Installs the portable BrightSync build that ships next to this script.
#
#   ./install.sh                 per-user install under ~/.local (no root needed)
#   sudo ./install.sh --system   system-wide install under /opt, with udev rule and i2c-dev autoload
#   sudo ./install.sh --setup-i2c   only install the udev rule and i2c-dev autoload (after a per-user install)
#   ./install.sh --uninstall     remove the per-user install (add --system for the system install)
set -eu

here=$(cd "$(dirname "$0")" && pwd)
mode=user
action=install
for arg in "$@"; do
    case "$arg" in
        --system) mode=system ;;
        --uninstall) action=uninstall ;;
        --setup-i2c) action=setup-i2c ;;
        -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

if [ "$mode" = system ]; then
    app_dir=/opt/brightsync
    bin_dir=/usr/local/bin
    desktop_dir=/usr/share/applications
    icon_dir=/usr/share/icons/hicolor/128x128/apps
else
    data_home=${XDG_DATA_HOME:-$HOME/.local/share}
    app_dir=$HOME/.local/opt/brightsync
    bin_dir=$HOME/.local/bin
    desktop_dir=$data_home/applications
    icon_dir=$data_home/icons/hicolor/128x128/apps
fi

need_root() {
    if [ "$(id -u)" -ne 0 ]; then
        echo "This step needs root. Re-run with sudo." >&2
        exit 1
    fi
}

setup_i2c() {
    need_root
    install -D -m 0644 "$here/99-brightsync-i2c.rules" /usr/lib/udev/rules.d/99-brightsync-i2c.rules
    install -D -m 0644 "$here/brightsync-i2c.conf" /usr/lib/modules-load.d/brightsync-i2c.conf
    modprobe i2c-dev 2>/dev/null || echo "Could not load i2c-dev now; it loads on next boot." >&2
    if command -v udevadm >/dev/null 2>&1; then
        udevadm control --reload-rules 2>/dev/null || true
        udevadm trigger --subsystem-match=i2c-dev 2>/dev/null || true
    fi
    echo "DDC/CI access configured. Log out and back in if monitors are still not detected."
}

do_install() {
    [ "$mode" = system ] && need_root
    mkdir -p "$app_dir" "$bin_dir" "$desktop_dir" "$icon_dir"
    rm -rf "$app_dir"
    mkdir -p "$app_dir"
    cp -a "$here/app/." "$app_dir/"
    chmod 0755 "$app_dir/BrightSync"
    ln -sf "$app_dir/BrightSync" "$bin_dir/brightsync"
    install -m 0644 "$here/brightsync.png" "$icon_dir/brightsync.png"
    sed "s|^Exec=brightsync|Exec=$bin_dir/brightsync|" "$here/brightsync.desktop" >"$desktop_dir/brightsync.desktop"
    chmod 0644 "$desktop_dir/brightsync.desktop"
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$desktop_dir" 2>/dev/null || true
    echo "Installed BrightSync to $app_dir (command: $bin_dir/brightsync)."

    if [ "$mode" = system ]; then
        setup_i2c
    else
        echo "External monitors need DDC/CI access. Run once: sudo $0 --setup-i2c"
    fi
    case ":$PATH:" in
        *":$bin_dir:"*) ;;
        *) echo "Note: $bin_dir is not on your PATH." ;;
    esac
}

do_uninstall() {
    [ "$mode" = system ] && need_root
    rm -rf "$app_dir"
    rm -f "$bin_dir/brightsync" "$desktop_dir/brightsync.desktop" "$icon_dir/brightsync.png"
    if [ "$mode" = system ]; then
        rm -f /usr/lib/udev/rules.d/99-brightsync-i2c.rules /usr/lib/modules-load.d/brightsync-i2c.conf
        command -v udevadm >/dev/null 2>&1 && udevadm control --reload-rules 2>/dev/null || true
    fi
    rm -f "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/brightsync.desktop"
    echo "Removed BrightSync. Settings in ~/.config/BrightSync were kept."
}

case "$action" in
    install) do_install ;;
    uninstall) do_uninstall ;;
    setup-i2c) setup_i2c ;;
esac
