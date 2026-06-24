# Roadmap & Feature Backlog

This document tracks planned features and design decisions for BrightSync.

---

## 1. Planned / Future Features

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
- **Localization**: Resource-based UI translation capabilities for multi-language support (Lower Priority).

---

## 2. Out-of-Scope / Rejected Features

- **Global Hotkey Daemon**: *Rejected*. Instead of writing an internal hotkey binder, users can bind triggers using their preferred system tools (e.g., AutoHotkey, PowerToys Keyboard Manager) mapped to the BrightSync CLI command engine (`BrightSync.exe brightness up/down`).
- **Per-Monitor Quick Sliders**: *Rejected*. Contradicts the "set-and-forget" philosophy. Per-monitor brightness adjustments should be calibrated once in Settings via multipliers and limits.
- **Grouping Monitors**: *Rejected*. Simple per-monitor profile calibration eliminates the need for separate synchronization groups.
- **UI Animations**: *Rejected*. The UI is designed to be high-performance, responsive, and lightweight without heavy transitions.
