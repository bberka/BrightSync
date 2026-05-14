# BrightSync

![GitHub release (latest by date)](https://img.shields.io/github/v/release/bberka/BrightSync) ![GitHub top language](https://img.shields.io/github/languages/top/bberka/BrightSync) ![GitHub License](https://img.shields.io/github/license/bberka/BrightSync)

BrightSync is a Windows tray app that keeps the brightness of your DDC/CI-compatible external monitors aligned to one shared brightness value.

On laptops, that shared value usually comes from the normal Windows brightness slider. On desktops, or on systems where Windows does not expose a brightness slider, BrightSync provides its own tray popup so you can control all supported monitors from one place.

It also includes optional automatic brightness, idle dimming, per-monitor limits, and recovery features for monitors that forget their brightness after sleep or power changes.

## Screenshots

### Quick menu

![Quick menu screenshot](assets/quick_menu.png)

### Settings window

![Settings window screenshot](assets/settings_menu.png)

## What BrightSync Does

- Uses one global brightness value as the source for all enabled external monitors.
- Reads the Windows brightness level when an internal display exposes it.
- Falls back to BrightSync's own tray slider when Windows has no usable brightness control.
- Applies that value to external monitors through DDC/CI.
- Lets you adjust each monitor with its own enable flag, minimum, maximum, and multiplier.
- Can drive brightness automatically from a 24-hour curve instead of manual input.

## Features

- Works on both laptops and desktops
- Tray icon with quick brightness popup
- Sync with the Windows brightness slider when Windows exposes one
- Automatic brightness based on a smooth 24-hour curve
- Visual curve editor in Settings
- Optional lock that keeps automatic brightness enabled after manual Windows brightness changes
- Windows Energy Saver detection with configurable brightness reduction
- Quick "Eye Protection" mode (dimming) with configurable duration and reduction amount
- Quick "Brightness Boost" mode (brightening) with configurable duration and increase amount
- Per-monitor enable or disable control
- Per-monitor minimum brightness, maximum brightness, and multiplier
- Optional idle dimming after inactivity
- Optional pause while Windows is locked
- Optional brightness enforcement to re-apply values if a monitor changes them
- Layered monitor detection with WMI and DisplayConfig fallbacks
- Layered external brightness backends: low-level DDC/CI, Windows high-level monitor APIs, and write-only capability fallbacks
- Apple display and Apple Studio Display detection with backend diagnostics
- HDR-aware monitor metadata and safer enforcement behavior
- Per-monitor detection diagnostics in Settings
- Optional legacy DDC/CI detection mode for compatibility
- Refresh monitors from the tray or Settings window
- Start with Windows
- GitHub release update checks

## Requirements

- Windows
- One or more DDC/CI-compatible external monitors for external brightness control

Important notes:

- BrightSync controls external monitors through DDC/CI.
- Built-in laptop panels are handled through the normal Windows brightness APIs, not DDC/CI.
- A monitor may still appear in the app even if BrightSync cannot change its brightness.
- If Windows does not show a native brightness slider, use the BrightSync tray slider instead.

## Install

1. Open the [latest release](https://github.com/bberka/BrightSync/releases/latest).
2. Download the `.zip` file you want.
3. Extract it anywhere.
4. Run `BrightSync.exe`.

If you are unsure which package to pick, start with `windows-x64-self-contained`.

## Daily Use

1. Start BrightSync.
2. Change brightness with the Windows slider if your system has one.
3. Otherwise, use the BrightSync tray icon and quick popup slider.
4. Open `Settings` for monitor-specific options and advanced behavior.

Behavior to know:

- When automatic brightness is off, the slider works normally.
- When automatic brightness is on, BrightSync controls the brightness value and the slider becomes read-only.
- The quick popup includes `Automatic Brightness`, `Eye Protection`, and `Brightness Boost` toggles for fast control.
- Right-click the `Eye Protection` or `Brightness Boost` menus in the tray for time duration presets.
- Most settings changes are only persisted after clicking `Save`.

## Settings Overview

### Brightness

- Adjust the shared brightness value when automatic brightness is off.

### Monitor Configs

- Enable or disable individual monitors.
- Clamp each monitor with minimum and maximum brightness.
- Scale a monitor brighter or dimmer with a multiplier.
- Expand any monitor row to see detection diagnostics and fallback details.

### Automatic Brightness

- Enable a 24-hour brightness curve.
- Drag curve points in Settings to tune brightness through the day.
- Use `Lock automatic brightness` if you want BrightSync to ignore manual Windows brightness changes and immediately restore the automatic target.

### Other Options

- `Start with Windows`
- `Legacy DDC/CI detection`
- `Disable on lock screen`
- `Idle dimming`
- `Eye protection mode`
- `Brightness boost mode`
- `Energy saver reduction`
- `Brightness enforcement`
- `Check for updates`

## Compatibility and Troubleshooting

- BrightSync now uses a layered detection pipeline. It combines DDC/CI enumeration with DisplayConfig and WMI-based metadata fallbacks to improve monitor naming and connection detection.
- BrightSync also uses layered external brightness control detection. If a low-level DDC/CI brightness read fails, it can fall back to the Windows high-level monitor API or a write-only capabilities path when supported by the display.
- Apple displays, including Apple Studio Display when Windows exposes a usable brightness backend, are now identified more clearly in diagnostics.
- HDR-capable displays are detected through DisplayConfig. When HDR is active, BrightSync avoids aggressive brightness readback enforcement on that display.
- Open a monitor row in `Settings` to see which detection backend was used and what fallback path BrightSync took.
- If monitor detection is unreliable, enable `Legacy DDC/CI detection`, then refresh monitors or restart the app.
- `Legacy DDC/CI detection` keeps the older compatibility-focused enumeration path and may help on systems where richer metadata detection is unreliable.
- If `Disable on lock screen` is enabled, BrightSync pauses external monitor reads and writes while Windows is locked and refreshes monitors after unlock.
- Idle dimming can either scale targets down by a percentage or reduce each monitor to its configured minimum brightness.
- Energy saver reduction automatically dims monitors when Windows is in power saving mode.
- Eye protection mode provides temporary manual dimming that stacks with other reduction features.
- Brightness boost mode provides a temporary brightness increase that stacks with other features.
- Brightness enforcement helps recover from monitors that reset brightness after sleep, power cycling, or input changes.
- Automatic brightness recalculates through the day and after resume or system time changes.

## Configuration

BrightSync stores its configuration at:

`%APPDATA%\BrightSync\config.json`

## Updates

BrightSync can check GitHub releases for updates. If a newer version is found, it opens the releases page.

## Build Locally

Requirements:

- Windows
- .NET 10 SDK

Build:

```powershell
dotnet restore
dotnet publish BrightSync.csproj -c Release
```

## Release Automation

This repository uses GitHub Actions to build and publish releases.

- Workflow: [`.github/workflows/release.yml`](.github/workflows/release.yml)
- Automatic trigger: update `VERSION` and push to `main` or `master`
- Manual trigger: run the workflow from the GitHub Actions tab
- Output packages:
  - `win-x64` self-contained single-file
  - `win-x64` framework-dependent
  - `win-x86` self-contained single-file
  - `win-x86` framework-dependent

The workflow reads the version from `VERSION`, publishes the app, creates zip archives, and uploads them to the matching GitHub release.
