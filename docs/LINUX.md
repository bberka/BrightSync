# BrightSync on Linux

BrightSync runs on Linux desktops with X11 or Wayland (through XWayland). It controls external monitors with DDC/CI over `/dev/i2c-N` and laptop panels through `/sys/class/backlight`. No `ddcutil` or other helper program is needed.

> Status: every release is checked by unit tests (EDID, DRM discovery, DDC/CI framing, backlight, autostart), by the whole suite running natively inside six distributions, and by a launch smoke test that starts the app under a virtual display and queries it over the CLI. Real monitor and tray behavior depends on your hardware and desktop; please report what you see using the diagnostics export (Settings > About).

## Verified distributions

The test binary and the app run inside these official cloud images (QEMU, x86-64): all unit tests pass, the app starts under Xvfb, initializes the Avalonia tray, answers `brightsync status`, and exits on `brightsync app exit`.

| Distribution | libc | Result |
|---|---|---|
| Ubuntu 24.04 | glibc | pass |
| Debian 12 | glibc | pass |
| Fedora 44 | glibc | see table below |
| Alpine 3.24 | musl | pass |
| Arch Linux (rolling) | glibc | pass |
| openSUSE Leap 16.0 | glibc | see table below |

Repeat it yourself (about 10 to 15 minutes per distribution without hardware virtualization):

```bash
python tools/linux-vm-test/vmtest.py --distros ubuntu,debian,fedora,alpine,arch,opensuse --qemu /path/to/qemu
```

arm64 and arm builds are cross-published and covered by the same unit tests on the x64 host, but not run natively; the release workflow runs the arm64 `.deb` smoke test on an arm64 runner.

## Supported systems

| Package | Distributions | CPU |
|---|---|---|
| `.deb` | Debian, Ubuntu, Linux Mint, Pop!_OS, Raspberry Pi OS | x64, arm64, arm (armhf) |
| `.rpm` | Fedora, RHEL and clones, openSUSE, Mageia | x64, arm64, arm (armv7hl) |
| `.pkg.tar.zst` | Arch, Manjaro, EndeavourOS | x64, arm64, arm |
| `.apk` | Alpine, postmarketOS (musl) | x64, arm64, arm |
| `.AppImage` | any glibc distribution | x64, arm64, arm |
| `.tar.gz` | any distribution, per-user or system install | x64, arm64, arm, and musl variants |
| `linux-portable.tar.gz` | any distribution with a .NET 10 runtime | riscv64, loongarch64, x86, and everything above |

Needed on the system (the packages declare these): `fontconfig` and the X11 client libraries `libX11`, `libXext`, `libXcursor`, `libXi`, `libXrandr`, `libICE`, `libSM`; Wayland sessions also need XWayland. Minimal images, such as openSUSE's, often lack `libICE`/`libSM`, which makes startup fail with "Unable to load shared library libICE.so.6"; install them (`libICE6 libSM6` on openSUSE). Optional: `libXss` (idle detection fallback on X11), `xdg-utils` (opening links), `zenity` or `kdialog` (dialog when a second instance is started).

## Monitor access (DDC/CI)

External monitors are reached through the `i2c-dev` kernel module. The distribution packages and `install.sh --system` set this up:

- `/usr/lib/modules-load.d/brightsync-i2c.conf` loads `i2c-dev` at boot.
- `/usr/lib/udev/rules.d/99-brightsync-i2c.rules` tags `i2c-*` devices with `uaccess`, which gives the user logged in at the local seat read and write access through logind ACLs. No group membership is required.

Per-user installs (`./install.sh`) and AppImages cannot write system files. Run once:

```bash
sudo ./install.sh --setup-i2c      # from the extracted tarball
```

Check access:

```bash
lsmod | grep i2c_dev               # module loaded
ls -l /dev/i2c-*                   # a "+" after the mode means an ACL grants your user access
brightsync status                  # while BrightSync runs: monitor counts
```

If `uaccess` is unavailable (no systemd-logind), add yourself to a group instead:

```bash
sudo groupadd -f i2c && sudo usermod -aG i2c "$USER"
echo 'KERNEL=="i2c-[0-9]*", GROUP="i2c", MODE="0660"' | sudo tee /etc/udev/rules.d/99-brightsync-i2c-group.rules
```

Then log out and in again.

Things that stop DDC/CI from working regardless of permissions:

- **DDC/CI is switched off in the monitor's on-screen menu.** Enable it (often under "Setup" or "Other").
- **Docks, KVMs, MST hubs and some HDMI adapters** drop DDC traffic. Try a direct cable.
- **NVIDIA proprietary driver:** connectors may not expose an I2C bus. BrightSync then lists the monitor as not controllable; the row's detection details name the missing bus. Using `nvidia-drm.modeset=1` often exposes the buses.
- **Virtual machines** have no physical monitor to talk to.
- DisplayLink adapters do not carry DDC/CI.

The monitor row in Settings shows the detection path and why a monitor is not controllable.

## Laptop panels

Brightness is written to `/sys/class/backlight/<device>/brightness` when the user has permission. Otherwise BrightSync asks `systemd-logind` (`Session.SetBrightness`), which is allowed for the active session without extra setup. Distros without logind need a udev rule that grants write access to the backlight device, for example:

```
ACTION=="add", SUBSYSTEM=="backlight", RUN+="/bin/chgrp video $sys$devpath/brightness", RUN+="/bin/chmod g+w $sys$devpath/brightness"
```

BrightSync never writes raw value 0 for a non-zero request, because many panels switch off at 0.

## Tray icon

BrightSync uses the StatusNotifierItem protocol through Avalonia.

| Desktop | Tray |
|---|---|
| KDE Plasma, XFCE (with the status notifier plugin), Cinnamon, MATE, Budgie, LXQt, Deepin | Works out of the box |
| GNOME | Install the [AppIndicator and KStatusNotifierItem Support](https://extensions.gnome.org/extension/615/appindicator-support/) extension. Without it GNOME hides the icon; start BrightSync from the app grid and use the settings window, or run `brightsync brightness set 50` |
| Sway, Hyprland, i3 | Needs a bar with tray support, such as Waybar's `tray` module |

Left click opens the quick brightness popup on desktops that deliver it. Some desktops only open the menu, so the menu also has a "Quick Brightness" entry. Middle click does not exist in the protocol; use the "Settings" entry.

Set `BRIGHTSYNC_TRAY=avalonia` to force the Avalonia tray on Windows for testing.

## Wayland

BrightSync uses the X11 backend, which on Wayland runs through XWayland. Window positions are honored there, so the popup opens in the bottom-right of the work area (the panel edge is inferred from the gap between the screen and its work area). Pointer-based screen selection and the XScreenSaver idle fallback only see XWayland clients; on GNOME and KDE, idle time comes from the compositor service instead.

## Features that follow the desktop

| Feature | Source |
|---|---|
| Resume from sleep | logind `PrepareForSleep` |
| Pause while locked | logind session `Lock`/`Unlock`, GNOME and freedesktop screensaver `ActiveChanged` |
| Idle dimming | GNOME Mutter IdleMonitor, then KDE/freedesktop `GetSessionIdleTime`, then XScreenSaver |
| Ignore while media plays | MPRIS players on the session bus |
| Power Saver reduction | power-profiles-daemon `ActiveProfile == power-saver` |
| Display hot-plug | polling `/sys/class/drm` every 3 seconds |
| Clock changes | wall clock compared with the monotonic clock every 5 seconds |
| Start at login | `~/.config/autostart/brightsync.desktop` (uses `$APPIMAGE` when running as an AppImage) |
| Notifications | `org.freedesktop.Notifications`, `notify-send` as fallback |

Not available on Linux: refresh rate switching, ICC color profile assignment, HDR status, and automatic installation of updates. Their controls are hidden.

## Files and logs

| What | Where |
|---|---|
| Config | `~/.config/BrightSync/config.json` |
| Logs | `~/.config/BrightSync/Logs/brightsync.log` |
| Single-instance lock | `$XDG_RUNTIME_DIR/brightsync.lock` |
| Command server metadata | `~/.local/share/BrightSync/command-server.json` |

Run `brightsync` in a terminal to see log output live.

## Uninstall

```bash
sudo apt remove brightsync          # or dnf remove / pacman -R / apk del
./install.sh --uninstall            # per-user tarball install (add --system with sudo for system installs)
```

Settings in `~/.config/BrightSync` are kept.
