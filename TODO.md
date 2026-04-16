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

Expand monitor detection and brightness-control compatibility, similar to the broader approach used by tools such as Twinkle Tray.

Potential areas to support or improve:

- Apple Studio Displays
- DDC/CI over different transport paths
- High-level DDC/CI handling improvements
- HDR-aware behavior
- WMIC-based fallback detection
- WMI bridge methods
- `Win32_DisplayConfig`

Why this may help:

- Different monitor vendors and connection paths expose brightness control differently.
- Better detection increases the number of systems where BrightSync works well.
- This could reduce cases where a monitor is visible in the app but not controllable.

Possible implementation direction:

- Add a layered detection pipeline with multiple fallback methods.
- Store which backend detected a monitor for diagnostics.
- Expose a simple diagnostics view so users can see why a monitor is or is not controllable.

## 3. Bug: System Theme Sync

- When app is open and system theme changes, app does not update its own theme. It requires full app restart manually by user.
