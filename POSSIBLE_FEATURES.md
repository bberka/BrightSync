# Possible Features

This document collects feature ideas that could be added to BrightSync in future releases.

## Implemented

### Disable on Lock Screen

Implemented.

Current behavior:

- BrightSync can pause external monitor reads and writes while the Windows session is locked.
- After unlock, BrightSync refreshes monitors and resumes sync.
- The option is available in Settings as `Disable on lock screen`.

## 1. Localization

Add support for multiple UI languages.

Why this may help:

- Makes BrightSync easier to use for non-English speakers.
- Improves reach for releases and community adoption.

Possible scope:

- Localize tray menu text, settings window text, status messages, and update messages.
- Start with a resource-based system so adding languages stays maintainable.
- Ship English first as default, then add community-contributed translations.

## 2. Better Visible Tray Icon

Improve tray icon visibility across light and dark taskbars.

Why this may help:

- The current icon can be hard to see depending on Windows theme and contrast.
- Better visibility makes the app feel more polished and easier to discover quickly.

Possible ideas:

- Provide separate light and dark tray icon variants.
- Detect Windows theme and switch icon automatically.
- Consider a slightly bolder shape or filled icon for small-size readability.

## 3. Improved Detection Support

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

## 4. Legacy DDC/CI Detection Method

Add an optional legacy DDC/CI detection mode, based on the older detection behavior from v1.15.5.

Why this may help:

- Some older or unusual monitors may work better with the previous detection logic.
- Gives users a compatibility fallback without changing the default behavior for everyone.

Possible behavior:

- Add a settings switch such as `Use legacy DDC/CI detection`.
- Require a monitor refresh or app restart after changing the setting.
- Clearly label it as a compatibility option.

## 5. Idle Detection With Brightness Reduction

Detect when the computer is idle and temporarily reduce brightness.

Why this may help:

- Saves power and reduces eye strain when the PC is left unattended.
- Can complement automatic brightness instead of replacing it.

Requested behavior:

- When the computer becomes idle, reduce all brightness values to minimum or to a percentage of the intended value, such as `50%`.
- If media is playing, do not treat the system as idle.

Possible behavior:

- Add an idle timeout setting.
- Add idle action choices such as:
  - set all monitors to minimum brightness
  - scale target brightness to a configurable percentage
- Restore the normal target brightness when activity resumes.

Implementation notes:

- Idle detection would likely need Windows input idle time APIs.
- Media playback detection may need session/media API integration so video playback does not trigger dimming.

## 6. Open Feature Slot

Reserved for another future feature idea.
