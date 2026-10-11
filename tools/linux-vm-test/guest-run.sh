#!/bin/sh
# Runs inside the guest VM (started by cloud-init, see vmtest.py). Phases, each reporting BSTEST-<PHASE>-EXIT:
#   PKG      install the distro's native package on a bare system (the package manager must pull every
#            library the app needs), check installed files, launch /usr/bin/brightsync, uninstall, check cleanup
#   UNIT     install desktop libraries, run the xunit binary against the real Linux services
#   SMOKE    launch the self-contained app under Xvfb and talk to it over the CLI
#   TARBALL  portable archive: per-user install.sh, launch, uninstall, then --system install and uninstall
#   APPIMAGE (Ubuntu only) build an AppImage with make-appimage.sh and launch it
#   PORTABLE (Ubuntu only) install a .NET runtime and run the framework-dependent linux-portable archive
# Everything is written to a file and a pump copies new lines to the serial console, re-opening it each time
# (getty restarts after package installs hang up an open serial descriptor and would swallow later output).
exec >/tmp/bs.out 2>&1
(
    sent=0
    while :; do
        total=$(wc -l </tmp/bs.out)
        if [ "$total" -gt "$sent" ]; then
            sed -n "$((sent + 1)),${total}p" /tmp/bs.out >/dev/ttyS0
            sent=$total
        fi
        sleep 2
    done
) &

. /etc/os-release
export HOME=/root
HOST=http://10.0.2.2:${BSPORT:-8000}
echo "BSTEST-BEGIN $PRETTY_NAME $(uname -m) kernel $(uname -r)"

fetch() { python3 -c "import urllib.request,sys; sys.stdout.buffer.write(urllib.request.urlopen(sys.argv[1]).read())" "$1"; }

case "$ID" in alpine) variant=linux-musl-x64 ;; *) variant=linux-x64 ;; esac

# Only what is needed to host a virtual display and a session bus; no X client libraries on purpose,
# so the native package has to declare every library the app loads.
install_tools() {
    case "$ID" in
        ubuntu|debian)
            export DEBIAN_FRONTEND=noninteractive
            apt-get update -qq && apt-get install -y -qq xvfb dbus-x11 xauth file ;;
        fedora) dnf install -y -q --setopt=install_weak_deps=False xorg-x11-server-Xvfb xorg-x11-xauth dbus-daemon ;;
        alpine) apk add --no-cache xvfb xvfb-run xauth dbus dbus-x11 ;;
        arch) pacman -Sy --noconfirm --needed xorg-server-xvfb xorg-xauth dbus ;;
        opensuse*|sles) zypper -n install xorg-x11-server-Xvfb xvfb-run xauth dbus-1-x11 ;;
    esac
}

install_deps() {
    case "$ID" in
        ubuntu|debian)
            apt-get install -y -qq dbus-user-session libfontconfig1 libx11-6 libxcursor1 libxi6 libxrandr2 libxext6 libice6 libsm6 libgl1 libxss1 xdg-utils ;;
        fedora)
            dnf install -y -q --setopt=install_weak_deps=False dbus-x11 fontconfig libICE libSM libXcursor libXi libXrandr libXext libXScrnSaver mesa-libGL xdg-utils ;;
        alpine)
            apk add --no-cache fontconfig libx11 libice libsm libxcursor libxi libxrandr libxext libxscrnsaver mesa-gl icu-libs libgcc libstdc++ ttf-dejavu xdg-utils ;;
        arch)
            pacman -Sy --noconfirm --needed fontconfig libice libsm libxcursor libxi libxrandr libxext libxss libglvnd xdg-utils ;;
        opensuse*|sles)
            zypper -n install fontconfig libICE6 libSM6 libXcursor1 libXi6 libXrandr2 libXext6 libXss1 Mesa-libGL1 xdg-utils ;;
    esac
}

# $1 = executable. Starts it under Xvfb on a private session bus, waits for the tray, queries and stops it.
cat >/tmp/smoke.sh <<'SMOKE'
#!/bin/sh
exe=$1
logdir=$HOME/.config/BrightSync/Logs
rm -rf "$logdir"
$exe --autostart >/tmp/app.out 2>&1 &
app=$!
i=0
while [ $i -lt 150 ]; do
    grep -qs "Tray manager initialized" "$logdir"/brightsync*.log && break
    kill -0 "$app" 2>/dev/null || { echo "app exited early"; break; }
    sleep 2
    i=$((i + 1))
done
echo "--- app.out ---"
tail -n 15 /tmp/app.out
echo "--- status ---"
# The command server starts a moment after the tray; retry instead of racing it.
n=0
rc=1
while [ $n -lt 10 ]; do
    timeout 120 $exe status && { rc=0; break; }
    rc=$?
    n=$((n + 1))
    sleep 3
done
echo "STATUS-EXIT $rc"
# The exit command must be answered before the app goes away (it used to report a transport failure).
timeout 120 $exe app exit
echo "EXIT-CMD-RC $?"
sleep 5
if kill -0 "$app" 2>/dev/null; then echo "app still running after exit command"; kill "$app"; echo "EXIT-FORCED"; else echo "EXIT-CLEAN"; fi
SMOKE
chmod +x /tmp/smoke.sh

run_smoke() {
    if command -v dbus-run-session >/dev/null 2>&1; then
        timeout 900 xvfb-run -a dbus-run-session -- /tmp/smoke.sh "$1"
    else
        timeout 900 xvfb-run -a /tmp/smoke.sh "$1"
    fi
}

check_file() { if [ -e "$1" ]; then echo "FILE-OK $1"; else echo "FILE-MISSING $1"; return 1; fi; }

# $1 = smoke log. The app answered status, answered the exit command, and was gone afterwards.
smoke_passed() { grep -q "STATUS-EXIT 0" "$1" && grep -q "EXIT-CMD-RC 0" "$1" && grep -q "EXIT-CLEAN" "$1"; }

# The uaccess tag only works from a rules file that sorts before 73-seat-late.rules; udev must also accept
# the syntax where it can check it (udevadm verify, systemd 254 and later).
check_udev_rule() {
    rule=$1
    case "$(basename "$rule")" in
        [0-6][0-9]-*|7[0-2]-*) echo "UDEV-RULE-ORDER-OK" ;;
        *) echo "UDEV-RULE-ORDER-BAD $rule"; return 1 ;;
    esac
    if udevadm verify --help >/dev/null 2>&1; then
        if udevadm verify "$rule" >/tmp/udev-verify.log 2>&1; then echo "UDEV-RULE-VERIFY-OK"; else cat /tmp/udev-verify.log; echo "UDEV-RULE-VERIFY-BAD"; return 1; fi
    else
        echo "UDEV-RULE-VERIFY-SKIPPED (no udevadm verify)"
    fi
}

# ---------------------------------------------------------------- PKG
echo "BSTEST-TOOLS-BEGIN"
install_tools >/tmp/tools.log 2>&1 </dev/null
echo "BSTEST-TOOLS-EXIT $?"

echo "BSTEST-PKG-BEGIN"
case "$ID" in
    ubuntu|debian)  pkg=pkg.deb;         install_pkg() { DEBIAN_FRONTEND=noninteractive apt-get install -y -qq /tmp/pkg.deb; }; remove_pkg() { apt-get remove -y -qq brightsync; } ;;
    fedora)         pkg=pkg.rpm;         install_pkg() { dnf install -y -q --setopt=install_weak_deps=False /tmp/pkg.rpm; }; remove_pkg() { dnf remove -y -q brightsync; } ;;
    opensuse*|sles) pkg=pkg.rpm;         install_pkg() { zypper -n --no-gpg-checks install /tmp/pkg.rpm; }; remove_pkg() { zypper -n remove brightsync; } ;;
    arch)           pkg=pkg.pkg.tar.zst; install_pkg() { pacman -U --noconfirm /tmp/pkg.pkg.tar.zst; }; remove_pkg() { pacman -R --noconfirm brightsync; } ;;
    alpine)         pkg=pkg.apk;         install_pkg() { apk add --allow-untrusted /tmp/pkg.apk; }; remove_pkg() { apk del brightsync; } ;;
esac
pkg_ok=0
if fetch "$HOST/$pkg" >"/tmp/$pkg" && [ -s "/tmp/$pkg" ]; then
    install_pkg >/tmp/pkg-install.log 2>&1 </dev/null
    echo "PKG-INSTALL-EXIT $?"
    tail -n 5 /tmp/pkg-install.log
    check_file /usr/bin/brightsync && check_file /opt/brightsync/BrightSync \
        && check_file /usr/share/applications/brightsync.desktop \
        && check_file /usr/lib/udev/rules.d/60-brightsync-i2c.rules \
        && check_file /usr/lib/modules-load.d/brightsync-i2c.conf \
        && check_file /usr/share/icons/hicolor/128x128/apps/brightsync.png \
        && check_udev_rule /usr/lib/udev/rules.d/60-brightsync-i2c.rules && files_ok=1 || files_ok=0
    run_smoke /usr/bin/brightsync >/tmp/pkg-smoke.log 2>&1
    cat /tmp/pkg-smoke.log
    smoke_passed /tmp/pkg-smoke.log && smoke_ok=1 || smoke_ok=0
    remove_pkg >/tmp/pkg-remove.log 2>&1 </dev/null
    echo "PKG-REMOVE-EXIT $?"
    if [ ! -e /usr/bin/brightsync ] && [ ! -e /opt/brightsync/BrightSync ] && [ ! -e /usr/lib/udev/rules.d/60-brightsync-i2c.rules ]; then
        echo "PKG-REMOVE-CLEAN"; clean_ok=1
    else
        echo "PKG-REMOVE-LEFTOVERS"; clean_ok=0
    fi
    [ "$files_ok" = 1 ] && [ "$smoke_ok" = 1 ] && [ "$clean_ok" = 1 ] && pkg_ok=1
else
    echo "PKG-DOWNLOAD-FAILED $pkg"
fi
echo "BSTEST-PKG-EXIT $((1 - pkg_ok))"

# ---------------------------------------------------------------- UNIT + SMOKE (self-contained build)
echo "BSTEST-DEPS-BEGIN"
install_deps >/tmp/deps.log 2>&1 </dev/null
echo "BSTEST-DEPS-EXIT $?"
tail -n 3 /tmp/deps.log

mkdir -p /opt/bs
cd /opt/bs || exit 1
fetch "$HOST/bs-$variant.tar.gz" | tar xz
chmod +x tests/BrightSync.Tests app/BrightSync
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_TieredPGO=0 DOTNET_gcServer=0

echo "BSTEST-UNIT-BEGIN"
timeout 1500 tests/BrightSync.Tests -notrait "Category=Hardware" -notrait "Category=Integration"
echo "BSTEST-UNIT-EXIT $?"

echo "BSTEST-SMOKE-BEGIN"
run_smoke /opt/bs/app/BrightSync
echo "BSTEST-SMOKE-EXIT $?"

# ---------------------------------------------------------------- TARBALL (install.sh)
echo "BSTEST-TARBALL-BEGIN"
tar_ok=1
mkdir -p /tmp/tarball && cd /tmp/tarball || exit 1
if fetch "$HOST/portable-$variant.tar.gz" | tar xz && cd BrightSync-*; then
    ./install.sh >/tmp/tar-user.log 2>&1 </dev/null; echo "TARBALL-USER-INSTALL-EXIT $?"
    check_file "$HOME/.local/bin/brightsync" && check_file "$HOME/.local/share/applications/brightsync.desktop" || tar_ok=0
    grep -q "^Exec=$HOME/.local/bin/brightsync" "$HOME/.local/share/applications/brightsync.desktop" && echo "DESKTOP-EXEC-OK" || { echo "DESKTOP-EXEC-BAD"; tar_ok=0; }
    run_smoke "$HOME/.local/bin/brightsync" >/tmp/tar-smoke.log 2>&1
    smoke_passed /tmp/tar-smoke.log && echo "TARBALL-SMOKE-OK" || { cat /tmp/tar-smoke.log; tar_ok=0; }
    ./install.sh --uninstall >/tmp/tar-user-un.log 2>&1 </dev/null; echo "TARBALL-USER-UNINSTALL-EXIT $?"
    [ ! -e "$HOME/.local/bin/brightsync" ] && [ ! -e "$HOME/.local/opt/brightsync" ] && echo "TARBALL-USER-CLEAN" || { echo "TARBALL-USER-LEFTOVERS"; tar_ok=0; }

    ./install.sh --system >/tmp/tar-sys.log 2>&1 </dev/null; echo "TARBALL-SYSTEM-INSTALL-EXIT $?"
    check_file /opt/brightsync/BrightSync && check_file /usr/local/bin/brightsync \
        && check_file /usr/lib/udev/rules.d/60-brightsync-i2c.rules && check_file /usr/lib/modules-load.d/brightsync-i2c.conf || tar_ok=0
    ./install.sh --system --uninstall >/tmp/tar-sys-un.log 2>&1 </dev/null; echo "TARBALL-SYSTEM-UNINSTALL-EXIT $?"
    [ ! -e /opt/brightsync ] && [ ! -e /usr/local/bin/brightsync ] && [ ! -e /usr/lib/udev/rules.d/60-brightsync-i2c.rules ] \
        && echo "TARBALL-SYSTEM-CLEAN" || { echo "TARBALL-SYSTEM-LEFTOVERS"; tar_ok=0; }
else
    echo "TARBALL-DOWNLOAD-FAILED"; tar_ok=0
fi
echo "BSTEST-TARBALL-EXIT $((1 - tar_ok))"

# ---------------------------------------------------------------- APPIMAGE (Ubuntu only)
if [ "$ID" = ubuntu ]; then
    echo "BSTEST-APPIMAGE-BEGIN"
    ai_ok=0
    mkdir -p /tmp/ai && cd /tmp/ai || exit 1
    if fetch "$HOST/appimage-src.tar.gz" | tar xz \
        && fetch "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" >appimagetool \
        && chmod +x appimagetool; then
        APPIMAGETOOL=/tmp/ai/appimagetool sh -c 'bash /tmp/ai/repo/packaging/linux/make-appimage.sh /tmp/ai/app /tmp/ai/BrightSync.AppImage x86_64' >/tmp/ai/build.log 2>&1
        echo "APPIMAGE-BUILD-EXIT $?"
        tail -n 4 /tmp/ai/build.log
        if [ -x /tmp/ai/BrightSync.AppImage ]; then
            export APPIMAGE_EXTRACT_AND_RUN=1
            run_smoke /tmp/ai/BrightSync.AppImage >/tmp/ai/smoke.log 2>&1
            cat /tmp/ai/smoke.log
            smoke_passed /tmp/ai/smoke.log && ai_ok=1
        fi
    fi
    echo "BSTEST-APPIMAGE-EXIT $((1 - ai_ok))"
    unset APPIMAGE_EXTRACT_AND_RUN

    # ------------------------------------------------------------ PORTABLE (Ubuntu only)
    # The framework-dependent archive runs on a .NET runtime the user provides. Take Microsoft's, the way
    # someone on an architecture without a self-contained build would, and drive the app through its launcher.
    echo "BSTEST-PORTABLE-BEGIN"
    pt_ok=0
    mkdir -p /tmp/portable && cd /tmp/portable || exit 1
    command -v curl >/dev/null 2>&1 || apt-get install -y -qq curl >/dev/null 2>&1
    if fetch "https://dot.net/v1/dotnet-install.sh" >dotnet-install.sh \
        && bash dotnet-install.sh --runtime dotnet --channel 10.0 --install-dir /opt/dotnet >/tmp/portable/dotnet.log 2>&1 \
        && fetch "$HOST/portable-linux-portable.tar.gz" | tar xz && cd BrightSync-*; then
        export DOTNET_ROOT=/opt/dotnet PATH="/opt/dotnet:$PATH"
        ./install.sh >/tmp/portable/install.log 2>&1 </dev/null; echo "PORTABLE-INSTALL-EXIT $?"
        run_smoke "$HOME/.local/bin/brightsync" >/tmp/portable/smoke.log 2>&1
        cat /tmp/portable/smoke.log
        smoke_passed /tmp/portable/smoke.log && pt_ok=1
        ./install.sh --uninstall >/dev/null 2>&1 </dev/null
    else
        echo "PORTABLE-SETUP-FAILED"; tail -n 5 /tmp/portable/dotnet.log 2>/dev/null
    fi
    echo "BSTEST-PORTABLE-EXIT $((1 - pt_ok))"
fi

echo "BSTEST-DONE"
sleep 8
sync
poweroff
