# BrightSync

![GitHub release (latest by date)](https://img.shields.io/github/v/release/bberka/BrightSync) ![GitHub top language](https://img.shields.io/github/languages/top/bberka/BrightSync) ![GitHub License](https://img.shields.io/github/license/bberka/BrightSync)

BrightSync is a Windows app that keeps the brightness of your DDC/CI-compatible monitors in sync with a single global brightness value.

On laptops and other systems where Windows exposes the built-in brightness slider, BrightSync listens to that slider and mirrors the change to all supported external monitors. On desktops, or on systems where Windows does not provide a brightness slider, BrightSync gives you a tray icon with a quick brightness popup so you can control all supported monitors from one place.

BrightSync also includes an optional automatic brightness mode. When enabled, the app drives brightness from a smooth 24-hour curve so your displays brighten through the day and dim at night.

## Screenshots

### Quick Menu

![App Screenshot](assets/quick_menu.png)

### Settings Menu

![App Screenshot](assets/settings_menu.png)

## How It Works

- If Windows has an internal-display brightness control, BrightSync syncs that value to your external monitors.
- If Windows does not have a brightness control, BrightSync uses its own tray slider as the global brightness source.
- If automatic brightness is enabled, BrightSync updates the global brightness from its 24-hour curve instead of allowing manual slider control.
- BrightSync applies that global value to each enabled DDC/CI monitor.
- Per-monitor settings let you clamp or scale the final brightness for each display.

## Features

- Works on both laptops and desktops
- Syncs with the Windows brightness slider when Windows exposes one
- Tray icon with a quick popup slider for desktops and unsupported internal-brightness scenarios
- Optional automatic brightness mode with a smooth 24-hour curve
- Visual curve editor in Settings for tuning brightness through the day
- One global brightness value for all supported monitors
- Per-monitor enable or disable control
- Per-monitor minimum brightness
- Per-monitor maximum brightness
- Per-monitor brightness scaling multiplier
- Optional auto start with Windows
- Optional brightness enforcement that re-applies brightness if a monitor changes it
- Monitor refresh action from the tray or settings window
- Update check against GitHub releases

## Requirements

- Windows
- DDC/CI-compatible monitors for external brightness control

Notes:

- BrightSync can only control monitors that support DDC/CI brightness commands.
- If a monitor does not support DDC/CI, it will still appear in the app, but BrightSync cannot change its brightness.
- Windows may only show the native brightness slider on systems with a compatible internal display. When it does not, use the BrightSync tray slider instead.
- When automatic brightness is enabled, manual brightness sliders stay visible as read-only status indicators.

## Install

1. Go to the [latest release](https://github.com/bberka/BrightSync/releases/latest).
2. Download the newest `.zip` file that matches your system.
3. Extract the zip to any folder.
4. Run `BrightSync.exe`.

If you are not sure which file to choose, try the `windows-x64-self-contained` zip first.

## Usage

1. Start BrightSync.
2. Change the Windows brightness slider if your system has one.
3. If Windows does not expose a brightness slider, use the BrightSync tray icon and quick popup slider.
4. Open `Settings` to configure brightness behavior, monitor-specific behavior, and automatic brightness.

Quick menu:

- When automatic brightness is off, the quick menu slider works normally.
- When automatic brightness is on, the quick menu slider is disabled and shows the current brightness chosen by BrightSync.
- The quick menu also includes an `Automatic Brightness` switch so you can turn the feature on or off quickly.

In settings, you can:

- Use the normal brightness slider when automatic brightness is off
- Turn sync on or off for each monitor
- Set a minimum brightness per monitor
- Set a maximum brightness per monitor
- Apply a brightness multiplier per monitor
- Enable `Automatic Brightness`
- Adjust the automatic-brightness curve visually across `0-24` hours
- Enable `Start with Windows`
- Enable periodic brightness enforcement and choose its interval

Settings sections are organized as:

1. Normal brightness slider
2. Options
3. Monitor configs
4. Automatic brightness

## Updates

BrightSync checks GitHub releases for updates and opens the releases page when a newer version is available.

## Builds and Releases

This repository uses GitHub Actions for releases.

- Workflow file: [`.github/workflows/release.yml`](.github/workflows/release.yml)
- Trigger: update the `VERSION` file and push to `main` or `master`
- Manual trigger: run the workflow from the GitHub Actions tab
- Output: release zip files for `win-x64` and `win-x86`, in both self-contained and framework-dependent versions

The workflow publishes the app, creates zip files, and uploads them to the matching GitHub release.

## Build Locally

Requirements:

- Windows
- .NET 10 SDK

Build:

```powershell
dotnet restore
dotnet publish BrightSync.csproj -c Release
```
