#!/bin/sh
# Runs inside the guest VM: installs desktop libraries for the distro, runs the BrightSync unit tests
# against the real Linux services, then launches the app under a virtual display and queries it over the CLI.
# Everything goes to a file; a pump copies new lines to the serial console, re-opening it every time.
# (getty restarts after package installs hang up an open serial descriptor and would swallow later output.)
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
echo "BSTEST-BEGIN $PRETTY_NAME $(uname -m) kernel $(uname -r)"
export HOME=/root
HOST=http://10.0.2.2:${BSPORT:-8000}

fetch() { python3 -c "import urllib.request,sys; sys.stdout.buffer.write(urllib.request.urlopen(sys.argv[1]).read())" "$1"; }

echo "BSTEST-DEPS-BEGIN"
install_deps() {
case "$ID" in
    ubuntu|debian)
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq
        apt-get install -y -qq xvfb dbus-x11 dbus-user-session libfontconfig1 libx11-6 libxcursor1 libxi6 libxrandr2 libxext6 libice6 libsm6 libgl1 libxss1 xauth xdg-utils
        ;;
    fedora)
        dnf install -y -q --setopt=install_weak_deps=False xorg-x11-server-Xvfb xorg-x11-xauth dbus-daemon dbus-x11 fontconfig libICE libSM libXcursor libXi libXrandr libXext libXScrnSaver mesa-libGL xdg-utils
        ;;
    alpine)
        apk add --no-cache xvfb xvfb-run xauth dbus dbus-x11 fontconfig libx11 libice libsm libxcursor libxi libxrandr libxext libxscrnsaver mesa-gl icu-libs libgcc libstdc++ ttf-dejavu xdg-utils
        ;;
    arch)
        pacman -Sy --noconfirm --needed xorg-server-xvfb xorg-xauth dbus fontconfig libice libsm libxcursor libxi libxrandr libxext libxss libglvnd xdg-utils
        ;;
    opensuse*|sles)
        zypper -n install xorg-x11-server-Xvfb xvfb-run xauth dbus-1-x11 fontconfig libICE6 libSM6 libXcursor1 libXi6 libXrandr2 libXext6 libXss1 Mesa-libGL1 xdg-utils
        ;;
    *) echo "unknown distro $ID" ;;
esac
}
# Package managers print terminal queries when stdout is a tty and wait for an answer; use a file instead.
install_deps >/tmp/deps.log 2>&1 </dev/null
echo "BSTEST-DEPS-EXIT $?"
tail -n 3 /tmp/deps.log
free -m
dmesg 2>/dev/null | grep -i "out of memory\|killed process" | tail -3

case "$ID" in alpine) variant=linux-musl-x64 ;; *) variant=linux-x64 ;; esac
mkdir -p /opt/bs
cd /opt/bs
fetch "$HOST/bs-$variant.tar.gz" | tar xz
chmod +x tests/BrightSync.Tests app/BrightSync
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_TieredPGO=0 DOTNET_gcServer=0

echo "BSTEST-UNIT-BEGIN"
timeout 1500 tests/BrightSync.Tests -notrait "Category=Hardware" -notrait "Category=Integration"
echo "BSTEST-UNIT-EXIT $?"

echo "BSTEST-SMOKE-BEGIN"
cat >/tmp/smoke.sh <<'SMOKE'
#!/bin/sh
logdir=$HOME/.config/BrightSync/Logs
/opt/bs/app/BrightSync --autostart >/tmp/app.out 2>&1 &
app=$!
i=0
while [ $i -lt 150 ]; do
    grep -qs "Tray manager initialized" "$logdir"/brightsync*.log && break
    kill -0 "$app" 2>/dev/null || { echo "app exited early"; break; }
    sleep 2
    i=$((i + 1))
done
echo "--- app.out ---"
cat /tmp/app.out 2>/dev/null | tail -30
echo "--- log ---"
tail -n 40 "$logdir"/brightsync*.log 2>/dev/null
echo "--- status ---"
timeout 120 /opt/bs/app/BrightSync status
echo "STATUS-EXIT $?"
timeout 120 /opt/bs/app/BrightSync app exit
sleep 5
kill -0 "$app" 2>/dev/null && { echo "app still running after exit command"; kill "$app"; echo "EXIT-FORCED"; } || echo "EXIT-CLEAN"
SMOKE
chmod +x /tmp/smoke.sh
if command -v dbus-run-session >/dev/null 2>&1; then
    timeout 900 xvfb-run -a dbus-run-session -- /tmp/smoke.sh
else
    timeout 900 xvfb-run -a /tmp/smoke.sh
fi
echo "BSTEST-SMOKE-EXIT $?"
echo "BSTEST-DONE"
sleep 8
sync
poweroff
