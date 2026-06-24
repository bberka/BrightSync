# Roadmap & Feature Backlog

This document tracks completed milestones, planned features, and design decisions for BrightSync.

## Design Philosophy

BrightSync is built around the philosophy of **"Configure once, and never think about monitor brightness again."**
- **Set and Forget**: Brightness rules, limits (min/max), and curves are configured per-monitor once. The app handles the rest dynamically.
- **No Micromanagement**: Avoid unnecessary per-monitor brightness controls in the daily quick menus.
- **Extensible Integration**: Leverage a robust CLI engine rather than building bloated internal systems (e.g., custom hotkey managers).

---

## 1. Completed Features

- [x] **Native AOT Compilation**: Fully supported for single-file, zero-dependency publishing with low memory footprint and instant startup.
- [x] **DDC/CI Advanced Hardware Controls**: Native programmatic support for Contrast, Volume, RGB Gains, Color Presets, and active Input Source switching.
- [x] **CLI Command Routing**: Full resident-forwarded command-line engine for scripting brightness, presets, and app state triggers.
- [x] **System Theme Syncing**: Application theme automatically aligns with Windows settings dynamically without restarts.
- [x] **Compact Quick Menu**: A streamlined, lightweight tray popup interface.
- [x] **Auto-Update Flow & Installation**: Seamless background version checking and user-triggered automated setup.
- [x] **Periodic Monitor Refresh**: Auto-reconnects and recovers lost DDC/CI connections on a customizable interval.

---

## 2. Planned / Future Features

### Core Engine & Automation
- **Advanced Media Detection for Idle Dimming**: Prevent idle dimming when media is active using the Windows Media Transport Controls (`GlobalSystemMediaTransportControlsSessionManager`).
- **Location-Based Sunrise/Sunset**: Auto-adjust the 24-hour brightness curve based on local sunrise/sunset coordinates.
- **Customizable Curve Points**: Allow adding, deleting, or adjusting curve coordinates at irregular time intervals.
- **Brightness & Color Presets**: Add quick-switch presets (e.g., "Reading", "Gaming", "Standard") for master configurations.

### Advanced Color Control
- **Color Warmth / Blue Light Control**:
  - Add native warmth/temp control or sync DDC/CI presets automatically with Windows Night Light.
  - Further refine RGB Gain manual sliders.

### Monitor Support & Compatibility
- **Advanced Diagnostics**: Export raw DDC/CI capabilities and communication logs to help troubleshoot unsupported monitors.
- **HDR Policies**: Configurable behavior and brightness scaling when a monitor has HDR enabled.
- **Wider Connection Topology Support**: Improve handling of complex MST hubs, daisy chains, and USB-C docks.

### UI / UX
- **Tray Icon Tooltip Details**: Display master brightness percentage or active monitor states when hovering over the tray icon.
- **Localization**: Resource-based UI translation capabilities for multi-language support (Lower Priority).

---

## 3. Out-of-Scope / Rejected Features

- **Global Hotkey Daemon**: *Rejected*. Instead of writing an internal hotkey binder, users can bind triggers using their preferred system tools (e.g., AutoHotkey, PowerToys Keyboard Manager) mapped to the BrightSync CLI command engine (`BrightSync.exe brightness up/down`).
- **Per-Monitor Quick Sliders**: *Rejected*. Contradicts the "set-and-forget" philosophy. Per-monitor brightness adjustments should be calibrated once in Settings via multipliers and limits.
- **Grouping Monitors**: *Rejected*. Simple per-monitor profile calibration eliminates the need for separate synchronization groups.
- **UI Animations**: *Rejected*. The UI is designed to be high-performance, responsive, and lightweight without heavy transitions.
