using BrightSync.Platform;
using Microsoft.Win32;

namespace BrightSync.Core.Services;

/// <summary>Maps <see cref="SystemEvents"/> onto the platform event contract. Hooks the OS lazily on first subscription.</summary>
internal sealed class WindowsSystemEvents : ISystemEvents
{
    private readonly object _hookLock = new();
    private bool _hooked;

    private EventHandler? _resumed;
    private EventHandler<bool>? _sessionLockChanged;
    private EventHandler? _timeChanged;
    private EventHandler? _displayConfigurationChanged;

    public event EventHandler? Resumed
    {
        add { _resumed += value; EnsureHooked(); }
        remove => _resumed -= value;
    }

    public event EventHandler<bool>? SessionLockChanged
    {
        add { _sessionLockChanged += value; EnsureHooked(); }
        remove => _sessionLockChanged -= value;
    }

    public event EventHandler? TimeChanged
    {
        add { _timeChanged += value; EnsureHooked(); }
        remove => _timeChanged -= value;
    }

    public event EventHandler? DisplayConfigurationChanged
    {
        add { _displayConfigurationChanged += value; EnsureHooked(); }
        remove => _displayConfigurationChanged -= value;
    }

    private void EnsureHooked()
    {
        lock (_hookLock)
        {
            if (_hooked)
                return;

            _hooked = true;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.TimeChanged += OnTimeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _resumed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _sessionLockChanged?.Invoke(this, true);
                break;
            case SessionSwitchReason.SessionUnlock:
                _sessionLockChanged?.Invoke(this, false);
                break;
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e) => _timeChanged?.Invoke(this, EventArgs.Empty);

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => _displayConfigurationChanged?.Invoke(this, EventArgs.Empty);

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Moving/resizing the taskbar updates the work area through
        // WM_SETTINGCHANGE, which SystemEvents reports as General.
        if (e.Category == UserPreferenceCategory.General)
            _displayConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
}
