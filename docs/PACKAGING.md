# Packaging and releases

## Targets

| Runtime identifier | Build | Notes |
|---|---|---|
| `win-x64`, `win-arm64`, `win-x86` | Native AOT, single file | Inno Setup installer and zip |
| `linux-x64`, `linux-arm64` | Native AOT on a native runner | glibc |
| `linux-arm` | Self-contained single file, cross-published | glibc, armv7 |
| `linux-musl-x64`, `linux-musl-arm64`, `linux-musl-arm` | Self-contained single file, cross-published | Alpine and other musl systems |
| `linux-portable` | Framework-dependent, no runtime identifier | any architecture with a .NET 10 runtime and Skia natives: riscv64, loongarch64, x86 |

Not shipped as self-contained builds: `linux-x86`, `linux-riscv64`, `linux-loongarch64`, `linux-s390x`, `linux-ppc64le`. Microsoft publishes no .NET 10 runtime pack for them, so only the portable archive covers the first three (SkiaSharp and HarfBuzz ship natives for them). s390x and ppc64le have no Skia natives and are unsupported.

## Asset names

The names are a contract: the release workflow's `expected` list, the checksum manifest, `UpdateChecker.SelectInstallerAsset`, and the docs all rely on them.

| Asset | Name |
|---|---|
| Windows installer | `BrightSync-Setup-v<version>-win-<x64\|arm64\|x86>.exe` |
| Windows portable | `BrightSync-<version>-win-<x64\|arm64\|x86>.zip` |
| Linux glibc | `BrightSync-<version>-linux-<x64\|arm64\|arm>.<tar.gz\|deb\|rpm\|pkg.tar.zst\|AppImage>` |
| Linux musl | `BrightSync-<version>-linux-musl-<x64\|arm64\|arm>.<tar.gz\|apk>` |
| Linux portable | `BrightSync-<version>-linux-portable.tar.gz` |
| Checksums | `BrightSync-SHA256SUMS.txt` (every asset above, one line each) |

The Windows updater only accepts `BrightSync-Setup-*` assets whose name contains `-win-<arch>.`; do not rename them.

## Local builds

Windows (PowerShell, in a Developer prompt or with `vswhere` on `PATH` for Native AOT):

```powershell
dotnet publish src/BrightSync.App/BrightSync.App.csproj -c Release -r win-x64 -p:PublishSingleFile=true -o out
iscc /dAppVersion=0.19.0 /dPublishDir=out /dAppArch=x64 packaging/windows/installer.iss
```

Linux (needs `dotnet`, `clang`, `zlib1g-dev`; `nfpm` and `appimagetool` for those formats):

```bash
NFPM=nfpm APPIMAGETOOL=./appimagetool packaging/build-linux.sh linux-x64 0.19.0 out
BS_AOT=false packaging/build-linux.sh linux-arm 0.19.0 out        # cross build, no AOT
packaging/build-linux.sh linux-portable 0.19.0 out
```

`build-linux.sh` skips formats whose tool is missing and says so. Cross-publishing a Linux target from Windows works without AOT: `dotnet publish src/BrightSync.App/BrightSync.App.csproj -c Release -r linux-x64 -p:PublishAot=false -p:SelfContained=true -p:PublishSingleFile=true`.

## Linux package contents

`packaging/linux/` holds the shared files:

- `nfpm.yaml`: one definition for deb, rpm, apk, and archlinux. Installs the app to `/opt/brightsync`, links `/usr/bin/brightsync`, and adds the desktop file, icon, udev rule, and modules-load entry.
- `postinstall.sh`, `postremove.sh`: best-effort `modprobe i2c-dev`, udev reload, desktop database refresh. They never fail the transaction.
- `install.sh`: installer inside the portable tarball (`--system`, `--setup-i2c`, `--uninstall`).
- `99-brightsync-i2c.rules`, `brightsync-i2c.conf`, `brightsync.desktop`.

AppImage build uses `appimagetool` with an `AppRun` that launches the bundled binary. The AppImage cannot install the udev rule; its users run `--setup-i2c` from the tarball or follow [LINUX.md](LINUX.md).

## Release workflow

`.github/workflows/release.yml` runs: prepare (version from `VERSION` plus the next patch tag), wait for the matching CI run, validate (build and test on Windows and Ubuntu), package-windows (3 targets), package-linux (7 targets), smoke-linux (installs the x64 and arm64 `.deb` on a runner, starts the app under `xvfb` and `dbus-run-session`, and calls `brightsync status`), then release (verifies the exact asset list, writes `BrightSync-SHA256SUMS.txt`, uploads).

Authenticode signing of Windows binaries is optional and unchanged: set `WINDOWS_SIGNING_CERTIFICATE_BASE64`, `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`, and `WINDOWS_SIGNING_TIMESTAMP_URL` together. Linux packages are not signed; verify downloads with the checksum manifest.

`nfpm` is pinned and its download is verified against the published checksums. Action references are pinned to commit SHAs.
