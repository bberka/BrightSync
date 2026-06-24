using System.Diagnostics;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Updates;

public sealed class SelfUpdateService : IDisposable
{
    private const int IdleThresholdMinutes = 2;
    private readonly ConfigManager _config;
    private readonly HttpClient _httpClient;
    private readonly IdleReductionService _idleReduction;
    private readonly Timer _idleWatcher;

    private readonly UpdateChecker _updateChecker;
    private bool _disposed;
    private bool _isManualCheck;
    private string? _pendingInstallerPath;
    private bool _wasIdle;

    public SelfUpdateService(
        UpdateChecker updateChecker,
        IdleReductionService idleReduction,
        ConfigManager config)
    {
        _updateChecker = updateChecker;
        _idleReduction = idleReduction;
        _config = config;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BrightSync");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _idleWatcher = new Timer(_ => OnIdleWatcherTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsInstallPending => _pendingInstallerPath != null;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _updateChecker.UpdateAvailable -= OnUpdateAvailable;
        _idleWatcher.Dispose();
        _httpClient.Dispose();
        Log.Debug("Disposed self-update service");
    }

    public event EventHandler<string>? UpdateDownloaded;
    public event EventHandler? InstallStarted;
    public event EventHandler? InstallCompleted;
    public event EventHandler<string>? InstallFailed;

    public void Start()
    {
        _updateChecker.UpdateAvailable += OnUpdateAvailable;
        _idleWatcher.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Log.Information("Self-update service started. AutoInstall={AutoInstall}, Mode={Mode}",
            _config.Config.AutoInstallUpdates, _config.Config.AutoInstallMode);
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        _isManualCheck = true;
        try
        {
            return await _updateChecker.CheckNowAsync(force: true);
        }
        finally
        {
            _isManualCheck = false;
        }
    }

    public async Task<string?> DownloadUpdateAsync(GitHubRelease release, IProgress<int>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(release.InstallerDownloadUrl))
        {
            Log.Warning("No installer download URL available for release {TagName}", release.TagName);
            return null;
        }

        var tempDir = Path.GetTempPath();
        var installerPath = Path.Combine(tempDir, $"BrightSync_{release.TagName}_installer.exe");

        Log.Information("Downloading update {TagName} from {Url}", release.TagName, release.InstallerDownloadUrl);

        Directory.CreateDirectory(tempDir);

        using var response = await _httpClient.GetAsync(release.InstallerDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;

        await using var sourceStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None,
            8192, useAsync: true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;
        int lastReportedPercent = -1;

        while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;

            if (progress != null && totalBytes > 0)
            {
                var percent = (int)(totalRead * 100 / totalBytes);
                if (percent != lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    progress.Report(percent);
                }
            }
        }

        Log.Information("Update downloaded to {Path} ({TotalBytes} bytes)", installerPath, totalRead);
        return installerPath;
    }

    public void ScheduleIdleInstall(string installerPath)
    {
        _pendingInstallerPath = installerPath;
        Log.Information("Install scheduled for when system is idle. Path={Path}", installerPath);
    }

    public void InstallNow()
    {
        if (_pendingInstallerPath == null)
        {
            Log.Warning("InstallNow called but no installer is pending");
            return;
        }

        InstallStarted?.Invoke(this, EventArgs.Empty);
        Log.Information("Triggering install now. Path={Path}", _pendingInstallerPath);

        InstallCompleted?.Invoke(this, EventArgs.Empty);

        var psPath = GenerateInstallScript(_pendingInstallerPath);
        if (psPath == null)
        {
            var error = "Failed to generate install script";
            Log.Error(error);
            InstallFailed?.Invoke(this, error);
            return;
        }

        try
        {
            var currentProcessId = Environment.ProcessId;
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    $"-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File \"{psPath}\" {currentProcessId} \"{_pendingInstallerPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process.Start(psi);
            Log.Information("PowerShell install script launched. App will exit.");

            // Short delay to let PS process start, then exit
            Thread.Sleep(500);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            var error = $"Install failed: {ex.Message}";
            Log.Error(ex, error);
            InstallFailed?.Invoke(this, error);
        }
    }

    public async Task DownloadAndInstallAsync(GitHubRelease release, IProgress<int>? progress = null)
    {
        var installerPath = await DownloadUpdateAsync(release, progress);
        if (installerPath == null)
        {
            InstallFailed?.Invoke(this, "Download failed");
            return;
        }

        _pendingInstallerPath = installerPath;

        if (_config.Config.AutoInstallMode == AutoInstallMode.Instantly
            || _config.Config.AutoInstallMode == AutoInstallMode.WhenIdle)
        {
            InstallNow();
        }
    }

    private void OnUpdateAvailable(object? sender, UpdateCheckResult result)
    {
        if (_isManualCheck)
            return;

        if (!_config.Config.AutoInstallUpdates)
        {
            UpdateDownloaded?.Invoke(this,
                $"BrightSync v{result.LatestVersion} is available (current: v{result.CurrentVersion})");
            return;
        }

        if (_config.Config.AutoInstallMode == AutoInstallMode.Instantly)
        {
            Log.Information("Auto-install instantly requested. UI will handle progress-based installation.");
            return;
        }

        Log.Information("Auto-install triggered for update to {Version}", result.LatestVersion);
        _ = DownloadAndScheduleAsync();
    }

    private async Task DownloadAndScheduleAsync()
    {
        try
        {
            var release = await _updateChecker.GetLatestReleaseInfoAsync();
            if (release == null)
            {
                Log.Warning("Auto-install failed: could not fetch release info");
                return;
            }

            var installerPath = await DownloadUpdateAsync(release);
            if (installerPath == null)
                return;

            UpdateDownloaded?.Invoke(this, $"BrightSync v{release.Version} downloaded. Installing when idle.");

            if (_config.Config.AutoInstallMode == AutoInstallMode.Instantly)
            {
                InstallNow();
            }
            else
            {
                ScheduleIdleInstall(installerPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Auto-install download failed");
            InstallFailed?.Invoke(this, ex.Message);
        }
    }

    private void OnIdleWatcherTick()
    {
        if (_disposed)
            return;

        try
        {
            var idleDuration = _idleReduction.GetIdleDuration();
            var isIdle = idleDuration >= TimeSpan.FromMinutes(IdleThresholdMinutes);

            if (isIdle && !_wasIdle)
            {
                Log.Debug("System became idle (idle for {Duration})", idleDuration);
                TryIdleUpdateCheck();
            }

            if (isIdle && _pendingInstallerPath != null)
            {
                Log.Information("System idle and install pending; triggering install");
                InstallNow();
                return;
            }

            _wasIdle = isIdle;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Idle watcher tick failed");
        }
    }

    private async void TryIdleUpdateCheck()
    {
        try
        {
            if (!_config.Config.AutoCheckUpdates)
                return;

            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_config.Config.LastUpdateCheckDate == today)
                return;

            Log.Information("Running idle-triggered update check");
            await _updateChecker.CheckNowAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Idle-triggered update check failed");
        }
    }

    private string? GenerateInstallScript(string installerPath)
    {
        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), "BrightSync_update.ps1");
            var script =
                $$"""
                  param($ProcessId, $InstallerPath)
                  try { Wait-Process -Id $ProcessId -Timeout 30 -ErrorAction SilentlyContinue } catch {}
                  Start-Process -FilePath $InstallerPath -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -NoNewWindow
                  Remove-Item -LiteralPath $InstallerPath -Force -ErrorAction SilentlyContinue
                  Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
                  $appDir = "${env:ProgramFiles}\BrightSync"
                  if (Test-Path "$appDir\BrightSync.exe") { Start-Process -FilePath "$appDir\BrightSync.exe" }
                  """;
            File.WriteAllText(scriptPath, script);
            return scriptPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to generate install script");
            return null;
        }
    }
}