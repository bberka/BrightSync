using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using BrightSync.Platform;
using Serilog;

namespace BrightSync.UI;

/// <summary>
/// Cross-platform tray icon built on Avalonia's <see cref="TrayIcon"/> and <see cref="NativeMenu"/>.
/// Linux uses the StatusNotifierItem / dbusmenu protocols (KDE, XFCE, Cinnamon, MATE, GNOME with the
/// AppIndicator extension). Middle click is not delivered by those protocols, so the menu offers
/// "Settings" and "Quick Brightness" entries for the same actions.
/// </summary>
internal sealed class AvaloniaTrayIcon : ITrayIcon
{
    private static readonly int[] PresetHours = [1, 2, 3, 4, 8, 12, 24];

    private TrayIcon? _trayIcon;
    private NativeMenuItem? _eyeItem;
    private NativeMenuItem? _eyeToggle;
    private NativeMenuItem? _boostItem;
    private NativeMenuItem? _boostToggle;
    private bool _disposed;

    public event EventHandler? Clicked;
    public event EventHandler? MiddleClicked;
    public event EventHandler? SettingsRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? EyeProtectionToggleRequested;
    public event EventHandler? BrightnessBoostToggleRequested;
    public event EventHandler<int>? EyeProtectionPresetRequested;
    public event EventHandler<int>? BrightnessBoostPresetRequested;

    public void Initialize(string toolTip, bool eyeProtectionEnabled, bool brightnessBoostEnabled)
    {
        var application = Application.Current
                          ?? throw new InvalidOperationException("Avalonia application is not running.");

        _trayIcon = new TrayIcon
        {
            ToolTipText = toolTip,
            Icon = LoadIcon(),
            Menu = BuildMenu(),
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);

        TrayIcon.SetIcons(application, new TrayIcons { _trayIcon });
        UpdateMenuState(eyeProtectionEnabled, brightnessBoostEnabled);
        Log.Information("Avalonia tray icon initialized");
    }

    public void SetToolTip(string toolTip)
    {
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = toolTip;
    }

    public void UpdateMenuState(bool eyeProtectionEnabled, bool brightnessBoostEnabled)
    {
        if (_eyeItem is not null)
            _eyeItem.IsChecked = eyeProtectionEnabled;
        if (_eyeToggle is not null)
            _eyeToggle.IsChecked = eyeProtectionEnabled;
        if (_boostItem is not null)
            _boostItem.IsChecked = brightnessBoostEnabled;
        if (_boostToggle is not null)
            _boostToggle.IsChecked = brightnessBoostEnabled;
    }

    public void ShowNotification(string title, string message)
        => PlatformServices.Current.Shell.ShowNotification(title, message);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_trayIcon is null)
            return;

        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://BrightSync/Resources/app.png"));
        return new WindowIcon(stream);
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        menu.Add(Item("Quick Brightness", () => Clicked?.Invoke(this, EventArgs.Empty)));
        menu.Add(Item("Settings", () => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Add(new NativeMenuItemSeparator());

        var eyeMenu = new NativeMenu();
        _eyeToggle = Item("Toggle Eye Protection", () => EyeProtectionToggleRequested?.Invoke(this, EventArgs.Empty));
        _eyeToggle.ToggleType = MenuItemToggleType.CheckBox;
        eyeMenu.Add(_eyeToggle);
        eyeMenu.Add(new NativeMenuItemSeparator());
        AddPresets(eyeMenu, hours => EyeProtectionPresetRequested?.Invoke(this, hours));
        _eyeItem = new NativeMenuItem("Eye Protection") { Menu = eyeMenu, ToggleType = MenuItemToggleType.CheckBox };
        menu.Add(_eyeItem);

        var boostMenu = new NativeMenu();
        _boostToggle = Item("Toggle Brightness Boost", () => BrightnessBoostToggleRequested?.Invoke(this, EventArgs.Empty));
        _boostToggle.ToggleType = MenuItemToggleType.CheckBox;
        boostMenu.Add(_boostToggle);
        boostMenu.Add(new NativeMenuItemSeparator());
        AddPresets(boostMenu, hours => BrightnessBoostPresetRequested?.Invoke(this, hours));
        _boostItem = new NativeMenuItem("Brightness Boost") { Menu = boostMenu, ToggleType = MenuItemToggleType.CheckBox };
        menu.Add(_boostItem);

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Refresh Monitors", () => RefreshRequested?.Invoke(this, EventArgs.Empty)));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Exit", () => ExitRequested?.Invoke(this, EventArgs.Empty)));
        return menu;
    }

    private static void AddPresets(NativeMenu menu, Action<int> onPreset)
    {
        foreach (var hours in PresetHours)
        {
            var captured = hours;
            menu.Add(Item(captured == 1 ? "1 hour" : $"{captured} hours", () => onPreset(captured)));
        }
    }

    private static NativeMenuItem Item(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }
}
