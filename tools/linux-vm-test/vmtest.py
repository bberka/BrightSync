#!/usr/bin/env python3
"""Runs the BrightSync unit tests and a launch smoke test inside real Linux distributions.

For every distribution the script boots an official cloud image under QEMU, lets cloud-init run
guest-run.sh, and reads the verdict from the serial console. The guest checks, in order:

  PKG       the native package (.deb, .rpm, pacman, .apk) installs on a bare system with its dependencies
            resolved by the package manager, ships the expected files, launches, and uninstalls cleanly
  UNIT      the xunit suite runs against the real Linux services
  SMOKE     the self-contained app starts under Xvfb, answers `status`, and exits on `app exit`
  TARBALL   the portable archive installs per user and system-wide with install.sh and removes itself
  APPIMAGE  (Ubuntu) an AppImage is built from the published app and launched
  PORTABLE  (Ubuntu) the framework-dependent linux-portable archive runs on a freshly installed .NET runtime

Packages are produced with packaging/build-linux.sh, which needs bash and nfpm (--nfpm); without nfpm the
PKG phase reports a download failure and the run fails.

No administrator rights, WSL, Docker or hardware virtualization are needed: QEMU falls back to software
emulation (slow, about 10 to 15 minutes per distribution) and uses KVM or WHPX when you ask for it.

    python tools/linux-vm-test/vmtest.py --distros ubuntu,debian,fedora,alpine --qemu C:\\tools\\qemu

Requires: .NET 10 SDK, bash, nfpm, QEMU (qemu-system-x86_64 and qemu-img), Python 3.10+, about 3 GB of disk per image.
Images and logs are cached under <repo>/.vmtest (ignored by git).
"""
import argparse
import concurrent.futures
import http.server
import os
import shutil
import socketserver
import subprocess
import sys
import tarfile
import threading
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CRLF = bytes([13, 10])
LF = bytes([10])
HERE = Path(__file__).resolve().parent

IMAGES = {
    "ubuntu": ("ubuntu-24.04.img", "https://cloud-images.ubuntu.com/minimal/releases/noble/release/ubuntu-24.04-minimal-cloudimg-amd64.img"),
    "debian": ("debian-12.qcow2", "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-genericcloud-amd64.qcow2"),
    "fedora": ("fedora-44.qcow2", "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Cloud/x86_64/images/Fedora-Cloud-Base-Generic-44-1.7.x86_64.qcow2"),
    "alpine": ("alpine-3.24.qcow2", "https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/cloud/generic_alpine-3.24.1-x86_64-bios-cloudinit-r0.qcow2"),
    "arch": ("arch.qcow2", "https://geo.mirror.pkgbuild.com/images/latest/Arch-Linux-x86_64-cloudimg.qcow2"),
    "opensuse": ("opensuse-leap-16.qcow2", "https://download.opensuse.org/distribution/leap/16.0/appliances/Leap-16.0-Minimal-VM.x86_64-Cloud.qcow2"),
}
MUSL = {"alpine"}


def run(cmd, **kwargs):
    return subprocess.run(cmd, check=True, **kwargs)


def build_payload(work: Path, needed: set[str], nfpm: str | None, portable: bool) -> Path:
    """Publishes the test binary, the app and the Linux packages, and serves them to the guests."""
    serve = work / "serve"
    serve.mkdir(parents=True, exist_ok=True)
    version = (ROOT / "VERSION").read_text().strip() + ".0"
    bash = shutil.which("bash")
    for rid in needed:
        out = work / f"publish-{rid}"
        shutil.rmtree(out, ignore_errors=True)
        print(f"[payload] publishing {rid}")
        run(["dotnet", "publish", str(ROOT / "tests/BrightSync.Tests.csproj"), "-c", "Release", "-r", rid,
             "--self-contained", "true", "-p:BrightSyncTargetOs=linux", "-o", str(out / "tests"), "-v", "q"])
        run(["dotnet", "publish", str(ROOT / "src/BrightSync.App/BrightSync.App.csproj"), "-c", "Release", "-r", rid,
             "-p:PublishAot=false", "-p:SelfContained=true", "-p:PublishSingleFile=true", "-p:DebugType=None",
             "-o", str(out / "app"), "-v", "q"])
        with tarfile.open(serve / f"bs-{rid}.tar.gz", "w:gz") as tar:
            tar.add(out / "tests", arcname="tests")
            tar.add(out / "app", arcname="app")

        # Packages exactly as the release workflow builds them (no AOT: the host may not match the target).
        pkg_out = work / f"packages-{rid}"
        shutil.rmtree(pkg_out, ignore_errors=True)
        if not bash:
            sys.exit("bash not found; it is needed to run packaging/build-linux.sh")
        env = dict(os.environ, BS_AOT="false")
        if nfpm:
            wrapper = work / "nfpm-wrapper.sh"
            script = "\n".join(["#!/bin/sh", f'exec "{Path(nfpm).as_posix()}" "$@"', ""])
            wrapper.write_bytes(script.encode())
            env["NFPM"] = wrapper.as_posix()
        print(f"[payload] packaging {rid}")
        run([bash, str(ROOT / "packaging/build-linux.sh"), rid, version, str(pkg_out)], env=env)
        for source in pkg_out.iterdir():
            name = source.name
            if name.endswith(".tar.gz"):
                shutil.copyfile(source, serve / f"portable-{rid}.tar.gz")
            elif rid == "linux-x64" and name.endswith((".deb", ".rpm", ".pkg.tar.zst")):
                suffix = ".pkg.tar.zst" if name.endswith(".pkg.tar.zst") else Path(name).suffix
                shutil.copyfile(source, serve / f"pkg{suffix}")
            elif rid == "linux-musl-x64" and name.endswith(".apk"):
                shutil.copyfile(source, serve / "pkg.apk")

    if portable:
        pkg_out = work / "packages-linux-portable"
        shutil.rmtree(pkg_out, ignore_errors=True)
        print("[payload] packaging linux-portable")
        run([bash, str(ROOT / "packaging/build-linux.sh"), "linux-portable", version, str(pkg_out)])
        for source in pkg_out.glob("*.tar.gz"):
            shutil.copyfile(source, serve / "portable-linux-portable.tar.gz")

    if "linux-x64" in needed:
        # Source for the in-guest AppImage build: mirrors the repo paths make-appimage.sh resolves.
        with tarfile.open(serve / "appimage-src.tar.gz", "w:gz") as tar:
            tar.add(work / "publish-linux-x64" / "app", arcname="app")
            for rel in ("packaging/linux/make-appimage.sh", "packaging/linux/brightsync.desktop",
                        "src/BrightSync.UI/Resources/app.png"):
                data = (ROOT / rel).read_bytes()
                if rel.endswith((".sh", ".desktop")):
                    data = data.replace(CRLF, LF)
                info = tarfile.TarInfo(f"repo/{rel}")
                info.size = len(data)
                info.mode = 0o755 if rel.endswith(".sh") else 0o644
                import io
                tar.addfile(info, io.BytesIO(data))

    shutil.copyfile(HERE / "guest-run.sh", serve / "run.sh")
    # The guest shell needs LF endings even when this script runs on Windows.
    (serve / "run.sh").write_bytes((serve / "run.sh").read_bytes().replace(CRLF, LF))
    return serve


def download(work: Path, distro: str) -> Path:
    name, url = IMAGES[distro]
    path = work / "images" / name
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and path.stat().st_size > 50_000_000:
        return path
    if not url.startswith("https://"):
        sys.exit(f"refusing non-https image URL for {distro}")
    print(f"[image] downloading {distro}")
    urllib.request.urlretrieve(url, str(path) + ".part")
    os.replace(str(path) + ".part", path)
    return path


def boot(args, work: Path, serve: Path, distro: str, image: Path, port: int):
    qemu = Path(args.qemu) if args.qemu else None

    def tool(name):
        if qemu:
            for candidate in (qemu / name, qemu / f"{name}.exe"):
                if candidate.exists():
                    return str(candidate)
        found = shutil.which(name)
        if not found:
            sys.exit(f"{name} not found; pass --qemu <dir>")
        return found

    ci = serve / "ci" / distro
    ci.mkdir(parents=True, exist_ok=True)
    nl = chr(10)
    (ci / "meta-data").write_bytes(f"instance-id: bs-{distro}-{int(time.time())}{nl}local-hostname: bs-{distro}{nl}".encode())
    fetch = ("import urllib.request,sys; sys.stdout.buffer.write("
             f"urllib.request.urlopen('http://10.0.2.2:{port}/run.sh').read())")
    (ci / "user-data").write_bytes(
        ("#!/bin/sh" + nl + f"export BSPORT={port}" + nl + f'python3 -c "{fetch}" > /run-bs.sh' + nl + "sh /run-bs.sh" + nl).encode())

    overlay = work / "overlays" / f"{distro}.qcow2"
    overlay.parent.mkdir(parents=True, exist_ok=True)
    overlay.unlink(missing_ok=True)
    run([tool("qemu-img"), "create", "-q", "-f", "qcow2", "-F", "qcow2", "-b", str(image), str(overlay), "20G"])

    log = work / "logs" / f"{distro}.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    log.write_text("")

    class Handler(http.server.SimpleHTTPRequestHandler):
        def __init__(self, *a, **k):
            super().__init__(*a, directory=str(serve), **k)

        def log_message(self, format, *args):
            pass

    socketserver.ThreadingTCPServer.allow_reuse_address = True
    httpd = socketserver.ThreadingTCPServer(("127.0.0.1", port), Handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()

    cmd = [tool("qemu-system-x86_64"), "-machine", "q35", "-cpu", "max", "-smp", str(args.cpus), "-m", str(args.memory),
           "-accel", args.accel, "-drive", f"file={overlay},if=virtio,format=qcow2",
           "-netdev", "user,id=n0", "-device", "virtio-net-pci,netdev=n0",
           "-smbios", f"type=1,serial=ds=nocloud-net;s=http://10.0.2.2:{port}/ci/{distro}/",
           "-display", "none", "-monitor", "none", "-serial", f"file:{log}"]
    if qemu and (qemu / "share").exists():
        cmd += ["-L", str(qemu / "share")]

    proc = subprocess.Popen(cmd)
    deadline = time.time() + args.timeout * 60
    status = "TIMEOUT"
    while time.time() < deadline:
        time.sleep(10)
        if proc.poll() is not None:
            status = "VM-EXITED"
            break
        if "BSTEST-DONE" in log.read_text(errors="replace"):
            status = "DONE"
            time.sleep(15)
            break
    proc.kill()
    httpd.shutdown()
    return distro, status, log


def verdict(log: Path, distro: str) -> tuple[bool, str]:
    text = log.read_text(errors="replace")
    total = next((line.strip() for line in text.splitlines() if "Total:" in line and "Failed:" in line), "no unit test summary")
    # Every launch in the log ends with `app exit`; each of them has to be answered with exit code 0.
    exit_codes = [line.split()[-1] for line in text.splitlines() if line.startswith("EXIT-CMD-RC ")]
    phases = {
        "pkg": "BSTEST-PKG-EXIT 0" in text,
        "unit": "BSTEST-UNIT-EXIT 0" in text,
        "smoke": "STATUS-EXIT 0" in text and "EXIT-CLEAN" in text and "BSTEST-SMOKE-EXIT 0" in text
                 and bool(exit_codes) and all(code == "0" for code in exit_codes),
        "tarball": "BSTEST-TARBALL-EXIT 0" in text,
    }
    if distro == "ubuntu":
        phases["appimage"] = "BSTEST-APPIMAGE-EXIT 0" in text
        phases["portable"] = "BSTEST-PORTABLE-EXIT 0" in text
    detail = " ".join(f"{name}={'ok' if ok else 'FAIL'}" for name, ok in phases.items())
    return all(phases.values()), f"{detail} | {total.split('BrightSync.Tests')[-1].strip()}"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--distros", default="ubuntu,debian,fedora,alpine", help="comma separated: " + ",".join(IMAGES))
    parser.add_argument("--qemu", help="directory containing qemu-system-x86_64 and qemu-img (default: PATH)")
    parser.add_argument("--nfpm", help="path of the nfpm binary used to build deb/rpm/apk/pacman packages (default: PATH)")
    parser.add_argument("--accel", default="tcg,thread=multi,tb-size=512", help="QEMU -accel value, e.g. kvm or whpx")
    parser.add_argument("--parallel", type=int, default=2, help="VMs booted at once")
    parser.add_argument("--cpus", type=int, default=3)
    parser.add_argument("--memory", type=int, default=3072)
    parser.add_argument("--timeout", type=int, default=90, help="minutes per VM")
    parser.add_argument("--work", default=str(ROOT / ".vmtest"))
    args = parser.parse_args()

    work = Path(args.work)
    distros = [d.strip() for d in args.distros.split(",") if d.strip()]
    unknown = [d for d in distros if d not in IMAGES]
    if unknown:
        sys.exit(f"unknown distro(s): {unknown}")

    images = {d: download(work, d) for d in distros}
    serve = build_payload(work, {"linux-musl-x64" if d in MUSL else "linux-x64" for d in distros}, args.nfpm,
                          portable="ubuntu" in distros)

    results = []
    with concurrent.futures.ThreadPoolExecutor(args.parallel) as pool:
        futures = [pool.submit(boot, args, work, serve, d, images[d], 8100 + i) for i, d in enumerate(distros)]
        for future in futures:
            results.append(future.result())

    failed = False
    print()
    for distro, status, log in results:
        ok, detail = verdict(log, distro)
        failed |= not ok
        print(f"{'PASS' if ok else 'FAIL'}  {distro:10} {status:10} {detail}   (log: {log})")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
