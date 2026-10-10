using BrightSync.Core.Monitors;

namespace BrightSync.Platform;

/// <summary>Enumerates displays and performs brightness / VCP I/O for one operating system.</summary>
public interface IMonitorBackend : IDdcMonitorProvider
{
    bool SetBrightness(DdcMonitor monitor, int brightnessPercent);
    bool TryGetBrightness(DdcMonitor monitor, out int brightnessPercent);
    bool SetVcpFeature(DdcMonitor monitor, byte vcpCode, uint value);
    bool GetVcpFeature(DdcMonitor monitor, byte vcpCode, out uint currentValue, out uint maxValue);

    /// <summary>Drops any cached OS metadata so the next enumeration re-reads it.</summary>
    void InvalidateCaches();
}

/// <summary>One enumeration pass over connected monitors.</summary>
public interface IDdcMonitorProvider
{
    DdcMonitorSet Enumerate(bool useLegacyDetection, CancellationToken cancellationToken);
}

/// <summary>Built-in laptop panel brightness (WMI on Windows, backlight class on Linux).</summary>
public interface IInternalBrightness
{
    /// <summary>Current brightness 0-100, or -1 when unavailable.</summary>
    int ReadCurrentBrightness();

    bool TrySetBrightness(int brightnessPercent);
}

/// <summary>OS notifications that BrightSync reacts to. Handlers may run on any thread.</summary>
public interface ISystemEvents
{
    /// <summary>System resumed from sleep or hibernate.</summary>
    event EventHandler? Resumed;

    /// <summary>The user session was locked (true) or unlocked (false).</summary>
    event EventHandler<bool>? SessionLockChanged;

    /// <summary>The wall clock or time zone changed.</summary>
    event EventHandler? TimeChanged;

    /// <summary>Display topology, resolution, or work area (panel/taskbar) changed.</summary>
    event EventHandler? DisplayConfigurationChanged;
}

/// <summary>Reports whether the OS power saver mode (Windows Energy Saver, Linux power-saver profile) is on.</summary>
public interface IEnergySaverSource : IDisposable
{
    bool IsActive { get; }
    event EventHandler<bool>? Changed;
    void Start();
}

public interface IIdleTimeSource
{
    /// <summary>Time since the last keyboard/mouse input. <see cref="TimeSpan.Zero"/> when unknown.</summary>
    TimeSpan GetIdleTime();
}

public interface IMediaPlaybackSource
{
    /// <summary>True when any media session is actively playing.</summary>
    bool IsMediaPlaying();
}

public interface IAutoStartManager
{
    /// <summary>Registers or removes launching <c>BrightSync --autostart</c> at user login.</summary>
    void Apply(bool enable);
}

public interface IDisplaySettingsService
{
    IReadOnlyList<int> GetSupportedRefreshRates(string deviceName);
    int GetCurrentRefreshRate(string deviceName);
    bool SetRefreshRate(string deviceName, int refreshRate);
}

public interface IColorProfileService
{
    IReadOnlyList<string> GetInstalledColorProfiles();
    string GetActiveColorProfile(string deviceName);
    bool SetActiveColorProfile(string deviceName, string profileName);
}

/// <summary>Raw rectangle in virtual-screen pixels.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>Desktop-shell services: cursor, panel geometry, URLs, notifications, console, dialogs.</summary>
public interface IShellIntegration
{
    bool TryGetCursorPosition(out int x, out int y);

    /// <summary>Bounds of the taskbar/panel nearest the tray, when the OS can report them.</summary>
    bool TryGetTaskbarBounds(out ScreenRect bounds);

    void OpenUrl(string url);

    /// <summary>Opens the OS display settings page (HDR, resolution). No-op when the OS has none.</summary>
    void OpenDisplaySettings();

    /// <summary>Shows a desktop notification. Best effort.</summary>
    void ShowNotification(string title, string message);

    /// <summary>Shows a blocking informational message, used before any UI exists.</summary>
    void ShowMessage(string title, string message);

    /// <summary>Makes console output visible when launched from a terminal. Returns true when output will be seen.</summary>
    bool TryAttachParentConsole();
}

public interface ISingleInstanceGuard : IDisposable
{
    /// <summary>True when this process now owns the single-instance slot.</summary>
    bool TryAcquire();
}

/// <summary>System tray icon with context menu. Events fire on arbitrary threads.</summary>
public interface ITrayIcon : IDisposable
{
    event EventHandler? Clicked;
    event EventHandler? MiddleClicked;
    event EventHandler? SettingsRequested;
    event EventHandler? RefreshRequested;
    event EventHandler? ExitRequested;
    event EventHandler? EyeProtectionToggleRequested;
    event EventHandler? BrightnessBoostToggleRequested;
    event EventHandler<int>? EyeProtectionPresetRequested;
    event EventHandler<int>? BrightnessBoostPresetRequested;

    void Initialize(string toolTip, bool eyeProtectionEnabled, bool brightnessBoostEnabled);
    void SetToolTip(string toolTip);
    void UpdateMenuState(bool eyeProtectionEnabled, bool brightnessBoostEnabled);
    void ShowNotification(string title, string message);
}

/// <summary>Hands a verified update artifact to the OS installer. Absent when the OS has no self-install path.</summary>
public interface IUpdateInstaller
{
    /// <summary>File extension of the asset this installer consumes, e.g. <c>.exe</c>.</summary>
    string AssetExtension { get; }

    /// <summary>Asset-name token used in release files, e.g. <c>win</c> or <c>linux</c>.</summary>
    string AssetOsToken { get; }
}
