# BrightSync

BrightSync is a small Windows app that syncs your external monitor brightness with your laptop's internal display brightness.

## Install

1. Go to the [latest release](https://github.com/bberka/BrightSync/releases/latest).
2. Download the newest `.zip` file that matches your system.
3. Extract the zip to any folder.
4. Run `BrightSync.exe`.

If you are not sure which file to choose, try the `windows-x64-self-contained` zip first.

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
