# BrightSync agent notes

Avalonia tray app for Windows and Linux. Stable facts live in the code; this file holds what the code does not say.

## Platform rule

`src/` contains no OS API: no `DllImport`, registry, WMI, D-Bus, `Process.Start` for OS tools, or `OperatingSystem.IsX` branches. OS behavior goes behind a contract in `platform/BrightSync.Platform.Abstractions/Services/PlatformContracts.cs`, with one implementation per OS project and a flag in `PlatformCapabilities` when the UI must adapt. Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before adding or moving OS behavior.

Hardware code on Linux reads sysfs through `ISysfs` and talks to I2C through `II2cBus`; tests use `FakeSysfs` and `FakeDisplayBus` in `tests/Linux`. This machine cannot run Linux, so the fakes and the CI smoke job are the only runtime evidence.

## Verify

```
dotnet build BrightSync.sln
dotnet test --project tests/BrightSync.Tests.csproj --filter-not-trait "Category=Hardware" --filter-not-trait "Category=Integration"
dotnet test --project tests/BrightSync.Tests.csproj -p:BrightSyncTargetOs=linux ...   # same suite against the Linux services
dotnet build src/BrightSync.App/BrightSync.App.csproj -r linux-x64                                       # compile the other platform's graph
```

`global.json` selects Microsoft.Testing.Platform for xunit v3, so `dotnet test` needs `--project` and `--filter-not-trait` (the VSTest `--filter` fails). Native AOT publish on Windows needs `vswhere.exe` on `PATH` (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`). A BrightSync instance running on the machine holds the single-instance mutex, so a launch smoke test shows the "already running" dialog; use `BrightSync.exe status` against it, or ask before closing it.

## Gotchas

- The Windows tray stays Win32 (`WindowsTrayIcon`). Avalonia's tray lost the icon when the published app ran from another folder. Re-test in `C:\Program Files` after Native AOT before changing the default.
- Release asset names are a contract with `UpdateChecker`, the release workflow, and the docs. Read [docs/PACKAGING.md](docs/PACKAGING.md) before renaming or adding assets or touching workflows.
- Shell scripts and `packaging/linux/*` must stay LF (`.gitattributes`).
- Config JSON is shared by both OSes: keep property names (`StartWithWindows` stays, the UI label comes from `PlatformCapabilities.StartupLabel`).
- Linux user-facing behavior (permissions, tray support per desktop) is documented in [docs/LINUX.md](docs/LINUX.md); update it with any change there.
