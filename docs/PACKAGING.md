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
- `60-brightsync-i2c.rules`, `brightsync-i2c.conf`, `brightsync.desktop`.

`packaging/linux/make-appimage.sh` builds the AppImage (an `AppRun` that launches the bundled binary, packed by `appimagetool`, which needs the `file` command). `build-linux.sh` and the VM test harness both call it. The AppImage cannot install the udev rule; its users run `--setup-i2c` from the tarball or follow [LINUX.md](LINUX.md).

## Verifying packages

`python tools/linux-vm-test/vmtest.py --qemu <dir> --nfpm <nfpm>` builds the packages with `build-linux.sh`, boots each distribution, installs its native package on a bare system (the package manager must resolve every library), checks the installed files (including that the udev rule sorts before `73-seat-late.rules` and passes `udevadm verify` where available), launches `/usr/bin/brightsync`, uninstalls and checks nothing is left behind. It then runs the unit suite, the portable archive's `install.sh` (per user and `--system`), and on Ubuntu an AppImage build and launch plus the `linux-portable` archive on a freshly installed .NET runtime. Every launch ends with `brightsync app exit`, which has to return 0. See [LINUX.md](LINUX.md).

## Workflows

| Workflow | Runs on | Does |
|---|---|---|
| `ci.yml` | pull requests (any branch), pushes to `main`/`master` | Build and test on Windows, Ubuntu x64 and Ubuntu arm64; cross-compile both OS graphs. On a non-draft pull request it then calls `packages.yml` with the version `0.<VERSION>.0-pr<number>.<run>` and uploads `pr-meta` |
| `pr-packages.yml` | after `ci.yml` finishes for a pull request | Posts or updates one comment with the package list and a link to the run's artifacts |
| `packages.yml` | called by the two above | Builds every installer and package as artifacts named `BrightSync-package-<target>` and smoke-tests the Linux `.deb` on x64 and arm64. Publishes nothing |
| `release.yml` | `VERSION` changes on `main`/`master`, or manual dispatch from the default branch | prepare (version `0.<VERSION>.<next patch>`, 0ver: `VERSION` is only the minor number), wait for the matching CI run, validate (build and test on Windows and Ubuntu), call `packages.yml`, then verify the exact asset list, write `BrightSync-SHA256SUMS.txt` and upload to the GitHub Release |
| `website.yml` | changes under `website/` | Builds the site, deploys it to GitHub Pages from `main`/`master` |

A branch without a pull request runs nothing. Open a draft pull request to get the test legs without the package builds.

### Pull request builds on a public repository

Pull request code is untrusted, so the flow is split in two:

- `ci.yml` and `packages.yml` run on the `pull_request` event with a read-only token and no secrets. From a fork they cannot comment or sign, which is why the Windows signing steps are skipped.
- `pr-packages.yml` runs from the default branch with `pull-requests: write` after CI finishes. It never checks out or builds pull request code. It reads the `pr-meta` artifact (pull request number and commit), checks the number is digits, the commit is 40 hex characters, and the commit equals the pull request's current head, then renders only artifact names and sizes that match `BrightSync-package-[A-Za-z0-9.-]+`.

Recommended repository settings: Settings > Actions > General > "Require approval for first-time contributors" (or all outside collaborators), and keep the default read-only workflow token. Package artifacts need a GitHub login to download and are kept 14 days.

Do not move package builds to a self-hosted runner on a public repository: a pull request can run arbitrary code there. A self-hosted runner is reasonable only for the `Category=Hardware` tests (real monitors), limited to manual runs and branches in this repository.

Authenticode signing of Windows binaries is optional and unchanged: set `WINDOWS_SIGNING_CERTIFICATE_BASE64`, `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`, and `WINDOWS_SIGNING_TIMESTAMP_URL` together. Linux packages are not signed; verify downloads with the checksum manifest.

`nfpm` is pinned and its download is verified against the published `checksums.txt`. `appimagetool` is fetched from its `continuous` release (it publishes no versioned assets), so an AppImage can change if upstream does. Action references are pinned to commit SHAs and `.github/dependabot.yml` keeps NuGet, npm and Actions dependencies current.

nfpm only expands environment variables in `contents` entries that set `expand: true`, which is why `packaging/linux/nfpm.yaml` reads `NFPM_PUBLISH` that way. Its `archlinux` section uses `suggests` for optional packages (written as pacman `optdepends`).
