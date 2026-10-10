# Architecture

BrightSync is one Avalonia app that runs on Windows and Linux. It is split by responsibility, and every dependency points toward the contracts:

```
src/BrightSync.Platform.Abstractions   net10.0                       OS contracts, DdcMonitor model, VCP parsing, PlatformServices
src/BrightSync.Platform.Windows        net10.0-windows10.0.19041.0   Win32, WMI, dxva2, registry, WinRT media, Win32 tray
src/BrightSync.Platform.Linux          net10.0                       sysfs/DRM/EDID, i2c-dev, D-Bus, X11, XDG
src/BrightSync.Core                         net10.0                       sync engine, services, config, CLI, updates, diagnostics (no UI, no OS API)
src/BrightSync.UI                           net10.0                       Avalonia views, view models, tray manager, App
src/BrightSync.App                          follows the target OS         executable: Program, platform selection, AOT/trim/publish settings
tests/BrightSync.Tests                      follows the target OS         Core, UI, and both platform projects
```

```
App ──► UI ──► Core ──► Abstractions ◄── Windows | Linux (only one is referenced by the App)
 └──────────────────────────▲
```

- **Core** never references Avalonia or an OS project. The two places it needs the UI thread or the tray go through `IUiDispatcher` and `IResidentAppHost` (`UiDispatcher.cs`); the UI installs the Avalonia implementation at startup, and the default runs work inline, which is what tests rely on.
- **UI** never references an OS project. It reads `PlatformServices.Current` for shell actions and `PlatformCapabilities` for what to show.
- **App** is the only project that knows which OS project to link. Keep it thin: new behavior belongs in Core, UI, or a platform project.

## Repository layout

```
.github/workflows/   ci.yml, release.yml (packages), website.yml (GitHub Pages)
src/                 every production project: Core, UI, App, Platform.Abstractions, Platform.Windows, Platform.Linux
tests/               one xunit v3 project for all of the above, with Linux fakes in tests/Linux
packaging/           windows/ (Inno Setup script), linux/ (nfpm, desktop, udev, install.sh), build-linux.sh
tools/               developer tooling, such as linux-vm-test (QEMU distro runs)
website/             Astro landing page published to GitHub Pages; independent of the .NET build
docs/                guides (Linux, architecture, packaging, roadmap) and the screenshots the README uses
tasks/               planning notes for larger changes
Directory.Build.props, global.json, VERSION, BrightSync.sln at the root
```

Conventions: production code lives under `src/` and tests under `tests/`; project folders and assemblies share one
`BrightSync.<Part>` name; platform projects sit beside the code that uses them instead of in a separate tree, because
they are ordinary dependencies, only selected per OS by `Directory.Build.props`. Release tooling stays out of `src/`
so publishing concerns never leak into the libraries. The website has its own `package.json` and is never referenced
by the .NET solution.

## Target selection

`Directory.Build.props` sets `BrightSyncTargetOs` from the `-r` runtime identifier, else from the host OS (override with `-p:BrightSyncTargetOs=windows|linux`). The app and tests then:

- pick the matching TFM (`net10.0-windows10.0.19041.0` or `net10.0`),
- reference exactly one platform project (App) or both (tests), so the trimmer and Native AOT never see the other OS's code in a published app,
- define `PLATFORM_WINDOWS` or `PLATFORM_LINUX` in every project, which `src/BrightSync.App/PlatformBootstrap.cs` and the test initializer use to pick the factory.

The Windows project needs the Windows TFM for the WinRT media API, which is why a single `net10.0` app cannot reference it. The Linux project is plain `net10.0`, so the test project always references it and the Linux suite runs on a Windows machine too.

## Composition

`Program.Main` sets `PlatformServices.Current = PlatformBootstrap.Create()` first. `PlatformServices` is a bundle of interfaces:

| Contract | Windows | Linux |
|---|---|---|
| `IMonitorBackend` (enumerate, brightness, VCP I/O) | HMONITOR + dxva2 + WMI/DisplayConfig names | DRM connectors + EDID + `/dev/i2c-N` |
| `IInternalBrightness` | WMI `WmiSetBrightness` | `/sys/class/backlight`, logind fallback |
| `ISystemEvents` (resume, lock, clock, displays) | `SystemEvents` | logind and screensaver D-Bus, polls |
| `IEnergySaverSource` | `GUID_POWER_SAVING_STATUS` window messages | power-profiles-daemon |
| `IIdleTimeSource` | `GetLastInputInfo` | Mutter, KDE, XScreenSaver |
| `IMediaPlaybackSource` | GSMTC | MPRIS |
| `IAutoStartManager` | `HKCU\...\Run` | XDG autostart `.desktop` |
| `IShellIntegration` (cursor, panel, URLs, notifications, console) | Win32 | X11, `xdg-open`, D-Bus |
| `ISingleInstanceGuard` | named mutex (matches the Inno Setup `AppMutex`) | exclusive lock file |
| `IDisplaySettingsService`, `IColorProfileService` | ChangeDisplaySettings, WCS | empty implementations |
| `ITrayIcon` (optional native tray) | Win32 `Shell_NotifyIcon` | none, Avalonia tray is used |
| `IUpdateInstaller` | installer asset token `win` | none |
| `PlatformCapabilities` | flags and wording the UI reads | same |

Core services (`DdcCiService`, `BrightSyncEngine`, `AutoBrightnessService`, `IdleReductionService`, `PowerSavingService`, `ConfigManager`) default their dependencies to `PlatformServices.Current`. The test project sets a real bundle for the host in a module initializer, and tests that need fakes use the `internal` constructors that accept them.

`DdcCiService` owns locking, refresh coalescing, and monitor lifetime. The backend owns hardware access. `DdcMonitor.Resource` holds what must be released when a monitor set is replaced (Windows physical-monitor handle groups, Linux I2C file handles).

## Capabilities

The UI reads `PlatformCapabilities` and hides what an OS cannot do (refresh rate, color profiles, HDR button, automatic update install, legacy detection) and swaps wording (`StartupLabel`, `EnergySaverLabel`). It never shows a control that would silently fail.

On Linux some flags depend on the running desktop, not just the OS. `LinuxPlatform` asks both D-Bus buses which services exist (`LinuxDesktopFeatures.Detect`): the idle-dimming controls need Mutter, the KDE/freedesktop ScreenSaver service, or libXss; the Power Saver controls need power-profiles-daemon; the lock-pause option needs logind or a screensaver service; the media option needs a session bus. A minimal window-manager session therefore hides what it cannot honor.

## Tray

`TrayManager` creates the native tray when the platform offers one, otherwise `AvaloniaTrayIcon` (Avalonia `TrayIcon` and `NativeMenu`, StatusNotifierItem on Linux). Windows keeps the Win32 tray by default. The header comment in `WindowsTrayIcon.cs` records an Avalonia tray bug where the icon disappears when the published app runs from a different path. Before changing the Windows default, publish with Native AOT, move the folder to `C:\Program Files`, and confirm the icon appears. `BRIGHTSYNC_TRAY=avalonia` switches Windows to the Avalonia tray for that test.

## Updates

`UpdateChecker` picks release assets named `BrightSync-Setup-*-<os>-<arch>.exe` using the platform's `IUpdateInstaller.AssetOsToken`. Platforms without an installer (Linux) get an update notice only; `SelfUpdateService` is not started.

## Where to add things

- A new OS feature: add a contract to `PlatformContracts.cs`, implement it in each platform project, add it to `PlatformServices`, and add a capability flag if the UI must adapt.
- Pure logic shared by both OSes (VCP parsing, monitor names, identities) belongs in Abstractions with tests.
- Linux hardware code must take its filesystem and bus through `ISysfs` and `II2cBus`, so tests can use `FakeSysfs` and `FakeDisplayBus` in `tests/Linux`.
