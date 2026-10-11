using BrightSync.Core.Config;
using BrightSync.Platform;
using Serilog;

namespace BrightSync.Core.Brightness;

/// <summary>
/// Applies the configured brightness reduction while the OS power saver
/// (Windows Energy Saver, Linux power-saver profile) is active.
/// </summary>
public sealed class PowerSavingService : IDisposable
{
    public event EventHandler<bool>? EnergySaverStatusChanged;

    private readonly BrightSyncEngine _engine;
    private readonly ConfigManager _config;
    private readonly IEnergySaverSource _source;
    private bool _disposed;

    public bool IsEnergySaverActive => _source.IsActive;

    public PowerSavingService(BrightSyncEngine engine, ConfigManager config)
        : this(engine, config, PlatformServices.Current.CreateEnergySaverSource())
    {
    }

    internal PowerSavingService(BrightSyncEngine engine, ConfigManager config, IEnergySaverSource source)
    {
        _engine = engine;
        _config = config;
        _source = source;
    }

    public void Start()
    {
        _source.Changed += OnSourceChanged;
        _source.Start();

        Log.Information("Power saving service started. EnergySaverReductionEnabled={Enabled}",
            _config.Config.EnergySaverReductionEnabled);
    }

    private void OnSourceChanged(object? sender, bool isActive)
    {
        if (_config.Config.EnergySaverReductionEnabled)
            _engine.ForceSync();

        EnergySaverStatusChanged?.Invoke(this, isActive);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _source.Changed -= OnSourceChanged;
        _source.Dispose();
    }
}
