# Implementation Plan: Independent Brightness Control & Internal Monitor Configuration

This plan outlines the steps required to decouple BrightSync's master brightness from the Windows native slider and allow the internal monitor to be configured with per-monitor profiles (Min/Max/Multiplier).

## 1. Architectural Changes

### 1.1. Decoupling the Master Source
Currently, `InternalBrightnessWatcher` acts as the source of truth. When the Windows slider moves, the app reacts.
*   **Change**: Introduce a "Master Brightness" state within the app.
*   **Modes**:
    *   **Sync with Windows**: Maintained for backward compatibility. The Windows slider drives the app.
    *   **Independent**: The app slider drives everything. Changes to the Windows slider (via other apps or hardware keys) can optionally be ignored or used to update the app's master value.

### 1.2. Internal Monitor as a Target
Currently, `DdcCiService` focuses on external monitors. Internal monitors are detected but often skipped for control because they are assumed to be "the source".
*   **Change**: Treat the internal monitor as a controllable `DdcMonitor` target.
*   **Backend**: Implement a `WmiInternal` backend for `DdcMonitor` that uses WMI `WmiSetBrightness` to apply values.
*   **Configuration**: Allow the internal monitor to have a `MonitorProfile`, enabling it to stay at 20% when the master is 50%, or have a specific Max/Min.

## 2. Detailed Task List

### Phase 1: Configuration & Data Structures
- [ ] **AppConfig.cs**:
    - Add `MasterBrightnessMode` enum (`WindowsSync`, `Independent`).
    - Add `MasterBrightnessValue` (int) to persist the app's manual level.
    - Add `ControlInternalMonitor` (bool) to allow users to toggle internal monitor sync.
- [ ] **MonitorBrightnessBackend.cs**:
    - Add `InternalWmi` enum member.

### Phase 2: Core Logic Updates
- [ ] **DdcCiService.cs**:
    - Update `EnumerateMonitors` to ensure the internal panel is marked as `SupportsDdcCi = true` using the `InternalWmi` backend.
    - Implement `SetBrightness` logic for `InternalWmi`.
- [ ] **BrightSyncEngine.cs**:
    - Modify the sync loop to include internal monitors.
    - Prevent "Feedback Loops": When the app sets the internal monitor brightness (via WMI), it will trigger the Windows "Brightness Changed" event. The engine must distinguish between a user moving the Windows slider and the app moving it.
    - Implement `MasterBrightness` property that aggregates logic based on the selected mode.

### Phase 3: UI Enhancements
- [ ] **SettingsWindowViewModel.cs**:
    - Add UI to switch between `WindowsSync` and `Independent` modes.
    - Update the monitor list to display the internal monitor, allowing it to be enabled/disabled and configured like any other screen.
- [ ] **QuickBrightnessViewModel.cs**:
    - Update the main slider to control the internal state rather than calling `TrySetUserBrightness` (which targets WMI directly).
    - Ensure the internal monitor appears in the "Active Targets" list if applicable.

### Phase 4: Feedback Loop Management
- [ ] **InternalBrightnessWatcher.cs**:
    - Add a `SuppressEvents` flag or a way to ignore the next change event if it was triggered by the app's own `SetBrightness` call.

## 3. Risks & Mitigations
| Risk | Mitigation |
| :--- | :--- |
| **Feedback Loop** | The app sets brightness -> Windows fires event -> App thinks user moved slider -> App sets brightness again. Use a timestamp or flag to ignore WMI events for ~500ms after an app-initiated change. |
| **Desktop PCs** | On desktops, there is no internal monitor. The logic must gracefully handle "0 internal monitors" while still allowing the app-master slider to drive external DDC/CI monitors. |
| **Power Management** | Windows might reset brightness on wake. The `EnforcementTimer` must be updated to respect the new Master Brightness state. |

## 4. Verification Plan
- [ ] **Unit Tests**: Test the `CalculateTarget` logic with internal monitor profiles.
- [ ] **Manual Test (Laptop)**: Verify that moving the app slider changes BOTH internal and external screens according to their individual multipliers.
- [ ] **Manual Test (Desktop)**: Verify that the app-master slider controls external monitors even when no internal panel exists.
- [ ] **Manual Test (Conflicts)**: Change the Windows slider via Settings. Verify if BrightSync adopts the value (Sync mode) or overrides it (Independent mode).
