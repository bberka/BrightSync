# Implementation Plan: Independent Brightness Control & Internal Monitor Configuration

This plan outlines the steps required to decouple BrightSync's master brightness from the Windows native slider and allow the internal monitor to be configured with per-monitor profiles (Min/Max/Multiplier).

> [!NOTE]
> This plan has been successfully implemented. Per the final requirements, the legacy sync mode with the Windows slider was completely removed/ignored, allowing independent master control across all screens (including the built-in laptop display via WMI).

## 1. Architectural Changes

### 1.1. Decoupling the Master Source
- **Change**: Introduce a "Master Brightness" state within the app.
- **Mode**: Independent master control (fully ignoring/decoupling from Windows slider changes).

### 1.2. Internal Monitor as a Target
- **Change**: Treat the internal monitor as a controllable target.
- **Backend**: WMI (`WmiSetBrightness`) controlled via `MonitorBrightnessBackend.InternalWmi`.
- **Configuration**: Expose profile parameters (Min/Max/Multiplier/Enable) for the internal display.

---

## 2. Detailed Task List

### Phase 1: Configuration & Data Structures
- [x] **AppConfig.cs**:
    - Add `MasterBrightness` to persist the app's manual level.
- [x] **MonitorBrightnessBackend.cs**:
    - Add `InternalWmi` enum member.

### Phase 2: Core Logic Updates
- [x] **DdcCiService.cs**:
    - Update `EnumerateMonitors` to ensure the internal panel is marked as `SupportsDdcCi = true` using the `InternalWmi` backend.
    - Implement `SetBrightness` and `TryGetBrightness` logic for `InternalWmi`.
- [x] **BrightSyncEngine.cs**:
    - Modify the sync loop to include internal monitors.
    - Prevent "Feedback Loops" by disabling WMI watcher event listening.
    - Implement `MasterBrightness` property.

### Phase 3: UI Enhancements
- [x] **SettingsWindowViewModel.cs**:
    - Update the monitor list to display the internal monitor, allowing it to be enabled/disabled and configured like any other screen.
- [x] **QuickBrightnessViewModel.cs**:
    - Update the main slider to control the master state.
    - Ensure the internal monitor appears in the "Active Targets" list.

### Phase 4: Feedback Loop Management
- [x] **InternalBrightnessWatcher.cs**:
    - Simplify class to be a pure WMI read/write utility, disabling background event modifications.

---

## 3. Risks & Mitigations
- **Feedback Loop**: Solved by ignoring incoming WMI event modifications entirely.
- **Desktop PCs**: Handled gracefully. If no internal display is detected, the app drives external DDC/CI monitors using the virtual master value.

---

## 4. Verification Plan
- [x] **Unit Tests**: Added coverage for curve evaluation and core target calculation logic in `BrightSync.Tests`.
- [x] **Manual Test (Laptop)**: verified master slider changes both screens.
- [x] **Manual Test (Desktop)**: verified master slider controls external monitors.
