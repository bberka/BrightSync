# Implementation Plan: Cross-platform BrightSync (Windows + Linux)

Branch: `feat/cross-platform-linux`. Task list: `tasks/todo.md`.

## Overview

BrightSync is a Windows-only Avalonia tray app (Native AOT). Goal: ship the same app on Linux without changing Windows behavior. OS code moves behind contracts into per-OS projects; the shared app composes one platform at startup. Packaging, CI, docs and dependencies follow.

Baseline before any change: `dotnet test` = 153 passed (Windows, `Category!=Hardware&Category!=Integration`).

## Architecture decisions

| Decision | Rationale |
|---|---|
| 3 new projects: `BrightSync.Platform.Abstractions` (net10.0), `BrightSync.Platform.Windows` (`net10.0-windows10.0.19041.0`), `BrightSync.Platform.Linux` (net10.0). App `src/BrightSync.csproj` keeps UI, CLI, engine, config. | Platform code is isolated and unit-testable; app has no `DllImport`/WMI/registry left. |
| App TFM follows target OS (`BrightSyncTargetOs` derived from `-r`, else host OS) in `Directory.Build.props`. | Windows project needs the Windows TFM (WinRT media API); a plain `net10.0` project cannot reference it (NU1201). |
| Platform chosen at startup by `PlatformServicesFactory` using `#if PLATFORM_WINDOWS/LINUX`; app references only the matching project. | Trimmer/AOT never sees the other OS's code. |
| Contracts are narrow and capability-based (`PlatformCapabilities`); UI hides what an OS cannot do. | "All features work" is honest per OS; no fake success. |
| `IMonitorBackend` owns enumeration + brightness + VCP I/O; `DdcCiService` keeps orchestration, locking, refresh coalescing. Shared VCP capability parsing/probing lives in Abstractions. | Windows logic moves verbatim; Linux reuses probe logic. |
| Tray: `ITrayIcon` with two impls. Win32 impl stays default on Windows; Avalonia `TrayIcon`+`NativeMenu` impl is default on Linux and opt-in on Windows (`BRIGHTSYNC_TRAY=avalonia`). Flip the Windows default only after the AOT + moved-folder check in `WindowsTrayIcon.cs` header passes. | User asked to evaluate Avalonia tray; header comment documents a Windows AOT path bug. |
| Linux monitor control: DDC/CI over `/dev/i2c-N` (direct ioctl, no `ddcutil` dependency), EDID from `/sys/class/drm`; internal panel via `/sys/class/backlight` with logind `SetBrightness` D-Bus fallback (no root). | AOT-safe, no external binaries, works unprivileged with a `uaccess` udev rule. |
| Linux system events through `Tmds.DBus.Protocol` (logind, UPower, power-profiles-daemon, MPRIS, Mutter idle); displays via Avalonia `Screens.Changed`. | Already a transitive Avalonia.FreeDesktop dependency; AOT-friendly. |
| Self-update: Windows keeps the hardened installer flow. Linux checks and notifies, installs only through the package manager / re-download. | In-place root-owned package updates are unsafe; security-reviewed flow is Windows-specific. |
| Config JSON stays byte-compatible (`StartWithWindows` name kept; UI says "Start with system"). Paths use .NET special folders (XDG on Linux). | Existing Windows users unaffected. |
| Support matrix = what .NET + SkiaSharp ship: win-x64/x86/arm64; linux-x64/arm64/arm; linux-musl-x64/arm64. AOT where a native runner exists (win-x64, win-arm64, linux-x64, linux-arm64), otherwise trimmed self-contained single-file. linux-x86/riscv64/loongarch64/ppc64le/s390x have no supported .NET runtime + Skia assets; documented, not shipped. | Honest scope; no unbuildable matrix entries. |
| Linux packages via one `nfpm` config → deb, rpm, apk, archlinux; plus AppImage and portable tar.gz with `install.sh`. | One definition for all distros and arches. |

## Dependency graph

```
P0 baseline+packages ─► P1 abstractions/projects ─► P1b move Windows code (refactor, tests green)
                                                    ├─► P2 Linux platform (monitors, events, shell, tray)
                                                    ├─► P3 UI capability gating + wording
                                                    └─► P4 packaging + CI (needs RID/layout decisions only)
P2,P3,P4 ─► P5 docs + AGENTS.md ─► P6 verification
```

## Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Linux cannot run here (WSL has no virtualization) | High | Pure-logic code (DDC packet codec, EDID, sysfs parsing, desktop-file writer) isolated behind injectable file/IO seams and unit-tested on Windows; ubuntu CI job builds, tests, publishes. Call out unverified runtime behavior in docs and final report. |
| Refactor regresses Windows | High | Move code verbatim first (P1b), run 153 tests + AOT publish win-x64 + launch smoke after each checkpoint. |
| Avalonia tray misbehaves under AOT on Windows | Med | Win32 tray stays default there. |
| GNOME hides SNI tray icons without extension | Med | Document AppIndicator extension; menu exposes "Quick brightness" so the app stays usable. |
| Wayland forbids absolute window placement | Med | Popup uses screen working-area inference; degrade to centered on compositor-managed placement. |
| NativeAOT cross arch/musl builds fail on CI | Med | Tier-2 targets use trimmed single-file; AOT only on native runners. |
| Package updates break build (Avalonia 12.1.4, Skia) | Low | Update in isolated first commit, run tests. |

## Open questions (defaults chosen, change on request)

- Push branch / run CI: not done without approval.
- Windows Avalonia tray default flip: pending manual verification.
- Linux ICC profile + refresh-rate switching: unsupported in v1 (capability flag false).
