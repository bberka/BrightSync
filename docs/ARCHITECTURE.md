# Architecture

BrightSync is one Avalonia app that runs on Windows and Linux. All operating-system code sits behind contracts in per-OS projects. The app project has no `DllImport`, registry, WMI, or D-Bus code.

```
platform/BrightSync.Platform.Abstractions   net10.0                        contracts, DdcMonitor model, VCP parsing, PlatformServices
platform/BrightSync.Platform.Windows        net10.0-windows10.0.19041.0    Win32, WMI, dxva2, registry, WinRT media, Win32 tray
platform/BrightSync.Platform.Linux          net10.0                        sysfs/DRM/EDID, i2c-dev, D-Bus, X11, XDG
src/BrightSync.csproj                       follows the target OS          UI, CLI, engine, config, updates
tests/BrightSync.Tests.csproj               follows the target OS          all of the above; Linux code is tested on every host
```

## Target selection

`Directory.Build.props` sets `BrightSyncTargetOs` from the `-r` runtime identifier, else from the host OS (override with `-p:BrightSyncTargetOs=windows|linux`). The app and tests then:

- pick the matching TFM (`net10.0-windows10.0.19041.0` or `net10.0`),
- reference exactly one platform project, so the trimmer and Native AOT never see the other OS's code,
- define `PLATFORM_WINDOWS` or `PLATFORM_LINUX`, which `src/PlatformBootstrap.cs` uses to pick the factory.

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

The UI reads `PlatformCapabilities` and hides what an OS cannot do (refresh rate, color profiles, HDR button, automatic update install) and swaps wording (`StartupLabel`, `EnergySaverLabel`). It never shows a control that would silently fail.

## Tray

`TrayManager` creates the native tray when the platform offers one, otherwise `AvaloniaTrayIcon` (Avalonia `TrayIcon` and `NativeMenu`, StatusNotifierItem on Linux). Windows keeps the Win32 tray by default. The header comment in `WindowsTrayIcon.cs` records an Avalonia tray bug where the icon disappears when the published app runs from a different path. Before changing the Windows default, publish with Native AOT, move the folder to `C:\Program Files`, and confirm the icon appears. `BRIGHTSYNC_TRAY=avalonia` switches Windows to the Avalonia tray for that test.

## Updates

`UpdateChecker` picks release assets named `BrightSync-Setup-*-<os>-<arch>.exe` using the platform's `IUpdateInstaller.AssetOsToken`. Platforms without an installer (Linux) get an update notice only; `SelfUpdateService` is not started.

## Where to add things

- A new OS feature: add a contract to `PlatformContracts.cs`, implement it in each platform project, add it to `PlatformServices`, and add a capability flag if the UI must adapt.
- Pure logic shared by both OSes (VCP parsing, monitor names, identities) belongs in Abstractions with tests.
- Linux hardware code must take its filesystem and bus through `ISysfs` and `II2cBus`, so tests can use `FakeSysfs` and `FakeDisplayBus` in `tests/Linux`.
