using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BrightSync.Core.Brightness;
using BrightSync.UI;

namespace BrightSync.UI.ViewModels;

/// <summary>
/// View model that owns the commands and observable state for the system tray
/// context menu. Bound from the XAML <see cref="TrayIcon"/>'s <c>NativeMenu</c>
/// declared in App.axaml. AOT-safe: no reflection, no dynamic, primitives only.
/// </summary>
public sealed class TrayMenuViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Hour values exposed as tray menu preset items. AOT-safe literal list.</summary>
    public static readonly int[] PresetHours = [1, 2, 3, 4, 8, 12, 24];

    private readonly BrightSyncEngine _engine;
    private readonly EyeProtectionService _eyeProtection;
    private readonly BrightnessBoostService _brightnessBoost;
    private readonly Action _openSettings;
    private readonly Action _exitApp;
    private readonly Action _toggleQuickPopup;
    private readonly Action _refreshMonitors;

    public TrayMenuViewModel(
        BrightSyncEngine engine,
        EyeProtectionService eyeProtection,
        BrightnessBoostService brightnessBoost,
        Action openSettings,
        Action exitApp,
        Action toggleQuickPopup,
        Action refreshMonitors)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _eyeProtection = eyeProtection ?? throw new ArgumentNullException(nameof(eyeProtection));
        _brightnessBoost = brightnessBoost ?? throw new ArgumentNullException(nameof(brightnessBoost));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _exitApp = exitApp ?? throw new ArgumentNullException(nameof(exitApp));
        _toggleQuickPopup = toggleQuickPopup ?? throw new ArgumentNullException(nameof(toggleQuickPopup));
        _refreshMonitors = refreshMonitors ?? throw new ArgumentNullException(nameof(refreshMonitors));

        OpenSettingsCommand = new RelayCommand(_openSettings);
        ExitCommand = new RelayCommand(_exitApp);
        ToggleQuickPopupCommand = new RelayCommand(_toggleQuickPopup);
        RefreshMonitorsCommand = new RelayCommand(_refreshMonitors);
        ToggleEyeProtectionCommand = new RelayCommand(() =>
            _eyeProtection.SetEnabled(!_eyeProtection.IsEnabled));
        ToggleBrightnessBoostCommand = new RelayCommand(() =>
            _brightnessBoost.SetEnabled(!_brightnessBoost.IsEnabled));

        // Preset hour commands: one per hour value. Bound from XAML by indexer.
        SetEyeProtectionPresetCommands = BuildPresetCommands(h => _eyeProtection.SetEnabled(true, h));
        SetBrightnessBoostPresetCommands = BuildPresetCommands(h => _brightnessBoost.SetEnabled(true, h));

        _engine.MasterBrightnessChanged += OnMasterBrightnessChanged;
        _eyeProtection.StateChanged += OnEyeProtectionStateChanged;
        _brightnessBoost.StateChanged += OnBrightnessBoostStateChanged;
    }

    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand ToggleQuickPopupCommand { get; }
    public RelayCommand RefreshMonitorsCommand { get; }
    public RelayCommand ToggleEyeProtectionCommand { get; }
    public RelayCommand ToggleBrightnessBoostCommand { get; }
    public IReadOnlyDictionary<int, RelayCommand> SetEyeProtectionPresetCommands { get; }
    public IReadOnlyDictionary<int, RelayCommand> SetBrightnessBoostPresetCommands { get; }

    public bool EyeProtectionEnabled => _eyeProtection.IsEnabled;

    public bool BrightnessBoostEnabled => _brightnessBoost.IsEnabled;

    public int MasterBrightness => _engine.MasterBrightness;

    /// <summary>Header text for the Eye Protection toggle item, prefixed with a check glyph when active.</summary>
    public string EyeProtectionToggleHeader => EyeProtectionEnabled ? "✓ Toggle Eye Protection" : "Toggle Eye Protection";

    /// <summary>Header text for the Brightness Boost toggle item, prefixed with a check glyph when active.</summary>
    public string BrightnessBoostToggleHeader => BrightnessBoostEnabled ? "✓ Toggle Brightness Boost" : "Toggle Brightness Boost";

    private static IReadOnlyDictionary<int, RelayCommand> BuildPresetCommands(Action<int> apply)
    {
        var dict = new Dictionary<int, RelayCommand>(PresetHours.Length);
        foreach (var hours in PresetHours)
        {
            // Capture the loop variable so each command is bound to its own hour value.
            var captured = hours;
            dict[captured] = new RelayCommand(() => apply(captured));
        }
        return dict;
    }

    private void OnMasterBrightnessChanged(object? sender, int brightness)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            OnChanged(nameof(MasterBrightness)));
    }

    private void OnEyeProtectionStateChanged(object? sender, bool enabled)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            OnChanged(nameof(EyeProtectionEnabled)));
    }

    private void OnBrightnessBoostStateChanged(object? sender, bool enabled)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            OnChanged(nameof(BrightnessBoostEnabled)));
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _engine.MasterBrightnessChanged -= OnMasterBrightnessChanged;
        _eyeProtection.StateChanged -= OnEyeProtectionStateChanged;
        _brightnessBoost.StateChanged -= OnBrightnessBoostStateChanged;
    }
}
