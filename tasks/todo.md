# Task list: cross-platform BrightSync

Plan: `tasks/plan.md`. Check boxes as each task's verification passes.
Standing verification after every task: `dotnet build BrightSync.sln` clean, `dotnet test tests/BrightSync.Tests.csproj --filter "Category!=Hardware&Category!=Integration"` green (baseline 153).

## Phase 0: Baseline
- [x] T0 Branch `feat/cross-platform-linux`, baseline 153 tests green.
- [ ] T1 Update packages (Avalonia 12.1.4, xunit.runner.visualstudio, SystemEvents, Serilog, WmiLight, test SDK, coverlet; GitHub Actions pins). Accept: restore+build+tests green; `dotnet list package --outdated` empty for direct refs. (S)

## Phase 1: Foundation (no behavior change on Windows)
- [ ] T2 `Directory.Build.props` (target OS, TFM, `PLATFORM_*` defines, shared warnings) + 3 new csproj skeletons in `BrightSync.sln`; app/test conditional references. Accept: build passes on Windows for `-r win-x64` and `-r linux-x64` (restore+build, no AOT). (M)
- [ ] T3 Abstractions: contracts (`IMonitorBackend`, `ISystemEventSource`, `IPowerStatusSource`, `IIdleTimeSource`, `IMediaPlaybackSource`, `IAutoStartManager`, `IShellIntegration`, `ISingleInstanceGuard`, `ITrayIcon`, `IDisplaySettingsService`, `IColorProfileService`, `PlatformCapabilities`, `PlatformServices`); move `DdcMonitor`, `MonitorBrightnessBackend`, `HdrDisplayInfo`, VCP constants, capability-string parser/probe. Accept: compiles; unit tests for parser moved/added. (M)
- [ ] T4 Windows project: move `NativeMethods`, monitor resolvers, WMI internal brightness, display settings, color profiles verbatim; implement `WindowsMonitorBackend`; `DdcCiService` uses `IMonitorBackend`. Accept: 153 tests green. (L→split in 2 commits)
- [ ] T5 Windows events/power/idle/media/autostart/shell/single-instance impls; services take interfaces (optional ctor param defaulting to `PlatformServices.Current`). Accept: 153 tests green; no `Microsoft.Win32`/`DllImport` left in app project. (L→split)
- [ ] T6 Tray abstraction: `ITrayIcon`, move `WindowsTrayIcon`, write `AvaloniaTrayIcon`, `TrayManager` uses interface + positioning via `IShellIntegration`. Accept: Windows launch smoke shows Win32 tray; tests green. (M)
- [ ] T7 Update flow platform-aware: asset selection (`win-*`, `linux-*`), `IUpdateInstaller` (Windows = existing; Linux = notify only), capability flags. Accept: UpdateChecker/SelfUpdate tests updated and green. (M)

### Checkpoint A (Windows parity)
- [ ] 153+ tests green; `dotnet publish -r win-x64 -c Release` (AOT) succeeds; published exe launches, tray present, CLI `status` works; app csproj has no OS-specific API.

## Phase 2: Linux platform
- [ ] T8 Linux factory + minimal services; app starts with tray + settings on Linux (capabilities mostly false). Accept: `dotnet build -r linux-x64` clean, unit tests for factory. (S)
- [ ] T9 EDID parser + DRM connector scan + monitor identity/connection type (`/sys/class/drm`). Accept: unit tests with fixture EDIDs/sysfs trees. (M)
- [ ] T10 DDC/CI over i2c-dev: packet codec (checksums, get/set VCP, capabilities fragments), device access, bus↔connector mapping, retry/timing. Accept: codec unit tests against spec vectors; fake device end-to-end test. (L→split)
- [ ] T11 Internal backlight (`/sys/class/backlight` + logind SetBrightness). Accept: sysfs fixture tests. (S)
- [ ] T12 Events: logind sleep/lock, UPower, power-profiles-daemon (Energy Saver), timedate, Avalonia `Screens.Changed`; idle (Mutter IdleMonitor → XScreenSaver → logind IdleHint); MPRIS playback. Accept: each source has an injectable transport; unit tests on parsing/mapping. (L→split)
- [ ] T13 Shell: XDG autostart, `xdg-open`, single instance (flock), already-running notice, taskbar/panel inference from Bounds vs WorkingArea, cursor position (X11 optional). Accept: unit tests (desktop file text, path logic). (M)
- [ ] T14 Linux tray polish: Avalonia tray menu parity (presets, toggles, quick brightness item for DEs that don't deliver Activate), icon sizes. Accept: build clean; menu model unit test. (S)

### Checkpoint B (Linux builds)
- [ ] `dotnet publish -r linux-x64` and `-r linux-arm64` (non-AOT, cross from Windows) succeed; Linux unit tests pass on Windows host; no Windows API reachable from Linux graph.

## Phase 3: UI
- [ ] T15 Gate UI by `PlatformCapabilities` (refresh rate, ICC, HDR info, auto-install); neutral wording ("Start with system", "System brightness", "Energy Saver / Power Saver"); `OpenDisplaySettings` per OS. Accept: view-model tests; Windows text unchanged where Windows-specific. (M)

## Phase 4: Packaging + CI
- [ ] T16 Windows: add win-x86 (installer arch, release manifest); keep zip + Inno for x64/x86/arm64; try AOT win-x86 locally. (M)
- [ ] T17 Linux assets: `packaging/linux/` (desktop file, icons, udev rule, modules-load, nfpm.yaml, AppImage recipe, install.sh/uninstall.sh, tar.gz layout, scripts `build-linux-packages.sh`). Accept: scripts shellcheck-clean; package layout reviewed. (L→split)
- [ ] T18 CI: matrix build/test windows+ubuntu; release matrix for all RIDs (AOT on native runners, trimmed single-file otherwise); nfpm + AppImage jobs; checksum manifest + expected asset list updated. Accept: workflow YAML lint (actionlint) clean. (L)
- [ ] T19 Runtime asset naming contract shared by workflows and `UpdateChecker` (document in `docs/PACKAGING.md`). (S)

## Phase 5: Docs
- [ ] T20 `docs/README.md` (platforms, install per distro, tray notes), `docs/LINUX.md`, `docs/ARCHITECTURE.md`, `docs/PACKAGING.md`, `docs/TODO.md`; root `AGENTS.md` (writing-for-agents style: only non-discoverable conventions). (M)

## Phase 6: Verification
- [ ] T21 Full pass: build matrix, tests, Windows AOT publish + launch, CLI smoke, review diff for Windows regressions, final report with unverified-on-hardware list. (M)
