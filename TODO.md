# Possible Features

This document collects feature ideas that could be added to BrightSync in future releases.

## 1. Localization

Add support for multiple UI languages.

Why this may help:

- Makes BrightSync easier to use for non-English speakers.
- Improves reach for releases and community adoption.

Possible scope:

- Localize tray menu text, settings window text, status messages, and update messages.
- Start with a resource-based system so adding languages stays maintainable.
- Ship English first as default, then add community-contributed translations.

## 2. Improved Detection Support

Implemented in the app:

- Layered monitor metadata detection with DisplayConfig and WMI fallbacks
- Win32 desktop-monitor naming fallback when primary WMI naming is missing
- Per-monitor diagnostics showing the backend and fallback path used

Still possible future follow-up:

- Apple Studio Displays
- DDC/CI over different transport paths
- High-level DDC/CI handling improvements
- HDR-aware behavior

## 3. Bug: System Theme Sync

- When app is open and system theme changes, app does not update its own theme. It requires full app restart manually by user.
