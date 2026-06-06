# Possible Features

This document collects feature ideas that could be added to BrightSync in future releases.

## 1. UI & UX Improvements

- **Global Hotkeys**: Add support for configurable global keyboard shortcuts to increase/decrease brightness, toggle Automatic Brightness, Eye Protection, or Brightness Boost modes.
- **Per-Monitor Quick Control**: Add individual brightness sliders for each enabled monitor in the tray's quick popup menu.
- **Tray Icon Customization**:
    - Show the current master brightness percentage on the tray icon or in a more detailed tooltip.
    - Dynamically update the tray icon's sun appearance (e.g., more/fewer rays) based on the current brightness level.
- **Compact Quick Menu**: Provide a compact mode for the tray popup, especially useful for users with many monitors.
- **Animation & Transitions**: Implement smooth, gradual transitions when brightness changes (e.g., when entering/exiting idle dimming or during automatic curve adjustments).
- **Localization**: (Planned) Add support for multiple UI languages using a resource-based system.
- **System Theme Sync**: (Bug) Ensure the app updates its theme automatically when Windows system theme changes without requiring a restart.

## 2. Core Engine & Features

- **Advanced Media Detection for Idle Dimming**: Replace the current placeholder with a robust implementation using `GlobalSystemMediaTransportControlsSessionManager` (WinRT) to reliably suppress dimming during video/audio playback.
- **Color Temperature / Blue Light Control**:
    - Sync monitor color temperature with Windows Night Light via DDC/CI VCP features.
    - Provide a manual "Warmth" slider for external monitors.
- **Monitor Grouping**: Allow users to group multiple monitors together to synchronize their settings (enable/disable, multipliers, limits) as a single unit.
- **Brightness Presets**: Create quick-switch profiles for different activities (e.g., "Gaming" with high brightness, "Reading" with low brightness and blue light filter).

## 3. Automatic Brightness Enhancements

- **Location-Based Sunrise/Sunset**: Allow users to provide their location (latitude/longitude or region) to calculate real-world sunrise and sunset times, automatically adjusting the default curve.
- **Customizable Curve Points**:
    - Allow users to add or remove points from the 24-hour brightness curve.
    - Support irregular time intervals between points.
- **Multiple Auto-Brightness Profiles**: Support different curves for different days of the week (e.g., Weekday vs. Weekend).

## 4. Monitor Support & Compatibility

- **Improved Detection Support**:
    - Wider Apple Studio Display validation on real hardware and alternate Windows connection paths.
    - More transport-specific handling for docks, USB-C alt-mode chains, and MST topologies.
    - Optional user-facing HDR policy controls to manage how BrightSync behaves when HDR is toggled.
- **Advanced Diagnostics**:
    - Export detailed DDC/CI capability reports for troubleshooting.
    - Provide a "Debug View" showing real-time DDC/CI communication logs.
- **DDC/CI Feature Support**: Explore adding support for other VCP features like Contrast, Volume (for monitors with speakers), or Input Source switching.

## 5. Maintenance & Stability

- **Automatic Crash Reporting**: (Optional) Add an opt-in mechanism to collect anonymous crash logs to improve app stability.
