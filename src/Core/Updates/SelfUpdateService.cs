using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Updates;

public sealed class SelfUpdateService : IDisposable
{
    private const int IdleThresholdMinutes = 2;
    private const string InstallerFileName = "installer.exe";
    private const string InstallScriptFileName = "run-install.ps1";
    private const string InstallStatusFileName = "install-status.txt";

    private readonly ConfigManager _config;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IdleReductionService? _idleReduction;
    private readonly Timer _idleWatcher;
    private readonly UpdateChecker? _updateChecker;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private readonly Action<int> _exitProcess;
    private readonly Action<TimeSpan> _sleep;
    private readonly string _stagingRoot;
    private readonly TimeSpan _downloadTimeout;
    private readonly long _maxDownloadBytes;
    private readonly long _maxManifestBytes;
    private readonly Dictionary<string, string> _verifiedArtifactChecksums = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;
    private bool _isManualCheck;
    private string? _pendingInstallerPath;
    private string? _pendingInstallerChecksum;
    private bool _wasIdle;

    public SelfUpdateService(
        UpdateChecker updateChecker,
        IdleReductionService idleReduction,
        ConfigManager config)
        : this(
            updateChecker,
            idleReduction,
            config,
            CreateHttpClient(),
            ownsHttpClient: true,
            static processStartInfo => Process.Start(processStartInfo),
            Environment.Exit,
            static delay => Thread.Sleep(delay),
            Path.Combine(Path.GetTempPath(), "BrightSync", "updates"),
            UpdateArtifactSecurity.DefaultDownloadTimeout,
            UpdateArtifactSecurity.DefaultMaxInstallerBytes,
            UpdateArtifactSecurity.DefaultMaxManifestBytes)
    {
    }

    internal SelfUpdateService(
        UpdateChecker? updateChecker,
        IdleReductionService? idleReduction,
        ConfigManager config,
        HttpClient httpClient,
        Func<ProcessStartInfo, Process?> processStarter,
        Action<int> exitProcess,
        Action<TimeSpan> sleep,
        string stagingRoot,
        TimeSpan downloadTimeout,
        long maxDownloadBytes,
        long maxManifestBytes)
        : this(
            updateChecker,
            idleReduction,
            config,
            httpClient,
            ownsHttpClient: false,
            processStarter,
            exitProcess,
            sleep,
            stagingRoot,
            downloadTimeout,
            maxDownloadBytes,
            maxManifestBytes)
    {
    }

    private SelfUpdateService(
        UpdateChecker? updateChecker,
        IdleReductionService? idleReduction,
        ConfigManager config,
        HttpClient httpClient,
        bool ownsHttpClient,
        Func<ProcessStartInfo, Process?> processStarter,
        Action<int> exitProcess,
        Action<TimeSpan> sleep,
        string stagingRoot,
        TimeSpan downloadTimeout,
        long maxDownloadBytes,
        long maxManifestBytes)
    {
        _updateChecker = updateChecker;
        _idleReduction = idleReduction;
        _config = config;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _processStarter = processStarter;
        _exitProcess = exitProcess;
        _sleep = sleep;
        _stagingRoot = Path.GetFullPath(stagingRoot);
        _downloadTimeout = downloadTimeout > TimeSpan.Zero
            ? downloadTimeout
            : throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        _maxDownloadBytes = maxDownloadBytes > 0
            ? maxDownloadBytes
            : throw new ArgumentOutOfRangeException(nameof(maxDownloadBytes));
        _maxManifestBytes = maxManifestBytes > 0
            ? maxManifestBytes
            : throw new ArgumentOutOfRangeException(nameof(maxManifestBytes));
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _idleWatcher = new Timer(_ => OnIdleWatcherTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsInstallPending => _pendingInstallerPath != null;

    public InstallState InstallState { get; private set; } = InstallState.Idle;

    public int? LastInstallerExitCode { get; private set; }

    public string? LastInstallError { get; private set; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_updateChecker is not null)
        {
            _updateChecker.UpdateAvailable -= OnUpdateAvailable;
        }

        _idleWatcher.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        Log.Debug("Disposed self-update service");
    }

    public event EventHandler<string>? UpdateDownloaded;
    public event EventHandler? InstallStarted;
    public event EventHandler? InstallCompleted;
    public event EventHandler<string>? InstallFailed;
    public event EventHandler<InstallStateChangedEventArgs>? InstallStateChanged;

    public void Start()
    {
        ReportPreviousInstallResult();
        if (_updateChecker is not null)
        {
            _updateChecker.UpdateAvailable += OnUpdateAvailable;
        }

        _idleWatcher.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Log.Information("Self-update service started. AutoInstall={AutoInstall}, Mode={Mode}",
            _config.Config.AutoInstallUpdates, _config.Config.AutoInstallMode);
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        if (_updateChecker is null)
        {
            throw new InvalidOperationException("Update checking is not configured.");
        }

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

    public Task<string?> DownloadUpdateAsync(
        GitHubRelease release,
        IProgress<int>? progress = null)
    {
        return DownloadUpdateAsync(release, progress, CancellationToken.None);
    }

    public async Task<string?> DownloadUpdateAsync(
        GitHubRelease release,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        string? stagingDir = null;
        try
        {
            if (!UpdateArtifactSecurity.IsAllowedInstallerUrl(release.InstallerDownloadUrl))
            {
                Log.Warning("Rejected update download URL for release {TagName}", release.TagName);
                return null;
            }

            if (!UpdateArtifactSecurity.TryGetInstallerAssetName(
                    release.InstallerAssetName,
                    release.InstallerDownloadUrl,
                    out var installerAssetName))
            {
                Log.Warning("Rejected update artifact name for release {TagName}", release.TagName);
                return null;
            }

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(_downloadTimeout);
            var operationToken = timeoutCancellation.Token;

            var expectedChecksum = release.InstallerSha256;
            if (!UpdateArtifactSecurity.IsValidSha256(expectedChecksum))
            {
                if (!UpdateArtifactSecurity.IsAllowedChecksumManifestUrl(release.ChecksumManifestUrl))
                {
                    Log.Warning("Rejected update {TagName}: no trusted checksum manifest or checksum was supplied", release.TagName);
                    return null;
                }

                var manifest = await DownloadManifestAsync(release.ChecksumManifestUrl!, operationToken);
                if (!UpdateArtifactSecurity.TryReadSha256Manifest(manifest, installerAssetName, out expectedChecksum))
                {
                    Log.Warning("Rejected update {TagName}: checksum manifest did not contain {AssetName}",
                        release.TagName,
                        installerAssetName);
                    return null;
                }
            }

            stagingDir = CreateStagingDirectory();
            var installerPath = Path.Combine(stagingDir, InstallerFileName);
            Log.Information("Downloading verified update {TagName} from {Url}",
                release.TagName,
                release.InstallerDownloadUrl);

            using var response = await SendReleaseRequestAsync(
                release.InstallerDownloadUrl,
                isChecksumManifest: false,
                operationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is > 0 && totalBytes > _maxDownloadBytes)
            {
                throw new InvalidDataException($"Installer response exceeds the {_maxDownloadBytes}-byte limit.");
            }

            await using var sourceStream = await response.Content.ReadAsStreamAsync(operationToken);
            await using var fileStream = new FileStream(
                installerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            long totalRead = 0;
            var lastReportedPercent = -1;
            while (true)
            {
                var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(), operationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                totalRead += bytesRead;
                if (totalRead > _maxDownloadBytes)
                {
                    throw new InvalidDataException($"Installer response exceeds the {_maxDownloadBytes}-byte limit.");
                }

                hash.AppendData(buffer, 0, bytesRead);
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), operationToken);

                if (progress is not null && totalBytes is > 0)
                {
                    var percent = Math.Min(99, (int)(totalRead * 100 / totalBytes.Value));
                    if (percent != lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        progress.Report(percent);
                    }
                }
            }

            if (totalRead == 0 || totalBytes is not null && totalRead != totalBytes.Value)
            {
                throw new InvalidDataException("Installer response was empty or truncated.");
            }

            await fileStream.FlushAsync(operationToken);
            var actualChecksum = Convert.ToHexString(hash.GetHashAndReset());
            var expectedChecksumBytes = Convert.FromHexString(expectedChecksum!);
            var actualChecksumBytes = Convert.FromHexString(actualChecksum);
            if (!CryptographicOperations.FixedTimeEquals(expectedChecksumBytes, actualChecksumBytes))
            {
                throw new InvalidDataException("Installer checksum verification failed.");
            }

            lock (_verifiedArtifactChecksums)
            {
                _verifiedArtifactChecksums[installerPath] = actualChecksum;
            }

            progress?.Report(100);
            Log.Information("Verified update downloaded to {Path} ({TotalBytes} bytes)", installerPath, totalRead);
            return installerPath;
        }
        catch (OperationCanceledException ex)
        {
            Log.Warning(ex, "Update download cancelled or timed out for {TagName}", release.TagName);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update download rejected for {TagName}", release.TagName);
            return null;
        }
        finally
        {
            if (stagingDir is not null && !IsVerifiedStagingDirectory(stagingDir))
            {
                CleanupStagingDirectory(stagingDir);
            }
        }
    }

    public void ScheduleIdleInstall(string installerPath)
    {
        if (!TryGetVerifiedChecksum(installerPath, out var checksum))
        {
            FailInstall("Install rejected because the artifact was not verified by this update service.");
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(installerPath);
            if (!UpdateArtifactSecurity.IsPathUnderRoot(fullPath, _stagingRoot)
                || !File.Exists(fullPath))
            {
                FailInstall("Install rejected because the staged artifact path is invalid.");
                return;
            }

            _pendingInstallerPath = fullPath;
            _pendingInstallerChecksum = checksum;
            SetInstallState(InstallState.Pending);
            Log.Information("Install scheduled for when system is idle. Path={Path}", fullPath);
        }
        catch (Exception ex)
        {
            FailInstall($"Install rejected because the staged artifact path is invalid: {ex.Message}");
        }
    }

    public void InstallNow()
    {
        if (_pendingInstallerPath is null)
        {
            Log.Warning("InstallNow called but no installer is pending");
            return;
        }

        var installerPath = _pendingInstallerPath;
        var expectedChecksum = _pendingInstallerChecksum;
        if (!UpdateArtifactSecurity.IsValidSha256(expectedChecksum)
            || !UpdateArtifactSecurity.IsPathUnderRoot(installerPath, _stagingRoot)
            || !File.Exists(installerPath))
        {
            FailInstall("Install rejected because the staged artifact is no longer valid.", installerPath);
            return;
        }

        SetInstallState(InstallState.Starting);
        Log.Information("Triggering install handoff. Path={Path}", installerPath);

        var scriptPath = GenerateInstallScript(installerPath, expectedChecksum!, out var statusPath);
        if (scriptPath is null)
        {
            FailInstall("Failed to generate install script", installerPath);
            return;
        }

        try
        {
            var currentProcessId = Environment.ProcessId;
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(currentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(installerPath);
            psi.ArgumentList.Add(expectedChecksum!);
            psi.ArgumentList.Add(statusPath);

            var process = _processStarter(psi);
            if (process is null)
            {
                throw new InvalidOperationException("The installer handoff process did not start.");
            }

            process.Dispose();
            _pendingInstallerPath = null;
            _pendingInstallerChecksum = null;
            SetInstallState(InstallState.ScriptStarted);
            InstallStarted?.Invoke(this, EventArgs.Empty);
            Log.Information("PowerShell install script started. Installer exit status will be recorded by the script.");

            // Keep the existing shutdown handoff: the script waits for this process to exit
            // before starting the installer, so it can replace the running application.
            _sleep(TimeSpan.FromMilliseconds(500));
            _exitProcess(0);
        }
        catch (Exception ex)
        {
            FailInstall($"Install failed: {ex.Message}", installerPath);
        }
    }

    public Task DownloadAndInstallAsync(
        GitHubRelease release,
        IProgress<int>? progress = null)
    {
        return DownloadAndInstallAsync(release, progress, CancellationToken.None);
    }

    public async Task DownloadAndInstallAsync(
        GitHubRelease release,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var installerPath = await DownloadUpdateAsync(release, progress, cancellationToken);
        if (installerPath is null)
        {
            InstallFailed?.Invoke(this, "Download failed");
            return;
        }

        ScheduleIdleInstall(installerPath);

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
            if (_updateChecker is null)
            {
                return;
            }

            var release = await _updateChecker.GetLatestReleaseInfoAsync();
            if (release is null)
            {
                Log.Warning("Auto-install failed: could not fetch release info");
                return;
            }

            var installerPath = await DownloadUpdateAsync(release);
            if (installerPath is null)
                return;

            UpdateDownloaded?.Invoke(this, $"BrightSync v{release.Version} downloaded. Installing when idle.");
            ScheduleIdleInstall(installerPath);

            if (_config.Config.AutoInstallMode == AutoInstallMode.Instantly)
            {
                InstallNow();
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
            if (_idleReduction is null)
                return;

            var idleDuration = _idleReduction.GetIdleDuration();
            var isIdle = idleDuration >= TimeSpan.FromMinutes(IdleThresholdMinutes);

            if (isIdle && !_wasIdle)
            {
                Log.Debug("System became idle (idle for {Duration})", idleDuration);
                TryIdleUpdateCheck();
            }

            if (isIdle && _pendingInstallerPath is not null)
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
            if (_updateChecker is null || !_config.Config.AutoCheckUpdates)
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

    private async Task<string> DownloadManifestAsync(string manifestUrl, CancellationToken cancellationToken)
    {
        using var response = await SendReleaseRequestAsync(
            manifestUrl,
            isChecksumManifest: true,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength > _maxManifestBytes)
        {
            throw new InvalidDataException($"Checksum manifest exceeds the {_maxManifestBytes}-byte limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await UpdateArtifactSecurity.ReadAtMostAsync(stream, _maxManifestBytes, cancellationToken);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
    }

    private async Task<HttpResponseMessage> SendReleaseRequestAsync(
        string url,
        bool isChecksumManifest,
        CancellationToken cancellationToken)
    {
        var isAllowedInitialUrl = isChecksumManifest
            ? UpdateArtifactSecurity.IsAllowedChecksumManifestUrl(url)
            : UpdateArtifactSecurity.IsAllowedInstallerUrl(url);
        if (!isAllowedInitialUrl || !Uri.TryCreate(url, UriKind.Absolute, out var currentUri))
        {
            throw new InvalidDataException("The update URL is not an allowed GitHub release URL.");
        }

        for (var redirect = 0; redirect <= UpdateArtifactSecurity.MaxRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                {
                    throw new InvalidDataException("The update server returned a redirect without a location.");
                }

                var redirectedUri = new Uri(currentUri, location);
                if (!UpdateArtifactSecurity.IsAllowedRedirectUrl(redirectedUri))
                {
                    throw new InvalidDataException("The update redirect target is not an allowed GitHub release host.");
                }

                currentUri = redirectedUri;
                continue;
            }

            return response;
        }

        throw new InvalidDataException("The update download exceeded the redirect limit.");
    }

    private string CreateStagingDirectory()
    {
        Directory.CreateDirectory(_stagingRoot);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var directory = Path.Combine(_stagingRoot, $"BrightSync-update-{Guid.NewGuid():N}");
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                return directory;
            }
        }

        throw new IOException("Could not create a unique update staging directory.");
    }

    private string? GenerateInstallScript(string installerPath, string expectedChecksum, out string statusPath)
    {
        statusPath = string.Empty;
        try
        {
            var stagingDir = Path.GetDirectoryName(installerPath);
            if (stagingDir is null || !UpdateArtifactSecurity.IsPathUnderRoot(stagingDir, _stagingRoot))
            {
                return null;
            }

            statusPath = Path.Combine(stagingDir, InstallStatusFileName);
            var scriptPath = Path.Combine(stagingDir, InstallScriptFileName);
            var script =
                $$"""
                param([int]$ProcessId, [string]$InstallerPath, [string]$ExpectedSha256, [string]$StatusPath)

                function Write-InstallStatus([string]$State, [string]$ExitCode, [string]$ErrorMessage) {
                  $safeError = ($ErrorMessage -replace '[\r\n]+', ' ')
                  @($State, $ExitCode, $safeError) | Set-Content -LiteralPath $StatusPath -Encoding UTF8
                }

                Write-InstallStatus 'script-started' '' ''
                try {
                  try { Wait-Process -Id $ProcessId -Timeout 30 -ErrorAction SilentlyContinue } catch {}
                  $actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $InstallerPath).Hash
                  if (-not $actualSha256.Equals($ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Installer checksum changed after download verification.'
                  }

                  $installerProcess = Start-Process -FilePath $InstallerPath -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -PassThru -Wait -NoNewWindow
                  $exitCode = $installerProcess.ExitCode
                  if ($exitCode -eq 0) {
                    Write-InstallStatus 'installer-completed' $exitCode ''
                  }
                  else {
                    Write-InstallStatus 'installer-failed' $exitCode "Installer exited with code $exitCode."
                  }
                }
                catch {
                  Write-InstallStatus 'installer-error' '' $_.Exception.Message
                }
                finally {
                  Remove-Item -LiteralPath $InstallerPath -Force -ErrorAction SilentlyContinue
                  Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
                }
                """;
            File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return scriptPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to generate install script");
            return null;
        }
    }

    private void ReportPreviousInstallResult()
    {
        if (!Directory.Exists(_stagingRoot))
        {
            return;
        }

        try
        {
            foreach (var stagingDir in Directory.EnumerateDirectories(_stagingRoot, "BrightSync-update-*"))
            {
                var statusPath = Path.Combine(stagingDir, InstallStatusFileName);
                if (!File.Exists(statusPath))
                {
                    continue;
                }

                var lines = File.ReadAllLines(statusPath);
                if (lines.Length == 0)
                {
                    continue;
                }

                var state = lines[0].Trim();
                var error = lines.Length > 2 ? lines[2].Trim() : string.Empty;
                int? exitCode = null;
                if (lines.Length > 1 && int.TryParse(lines[1], out var parsedExitCode))
                {
                    exitCode = parsedExitCode;
                }

                if (state.Equals("installer-completed", StringComparison.OrdinalIgnoreCase))
                {
                    SetInstallState(InstallState.InstallerCompleted, exitCode);
                    InstallCompleted?.Invoke(this, EventArgs.Empty);
                    CleanupStagingDirectory(stagingDir);
                }
                else if (state is "installer-failed" or "installer-error")
                {
                    var message = string.IsNullOrWhiteSpace(error)
                        ? "The installer did not complete successfully."
                        : error;
                    SetInstallState(InstallState.Failed, exitCode, message);
                    InstallFailed?.Invoke(this, message);
                    CleanupStagingDirectory(stagingDir);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read the previous installer status");
        }
    }

    private bool TryGetVerifiedChecksum(string installerPath, out string checksum)
    {
        lock (_verifiedArtifactChecksums)
        {
            return _verifiedArtifactChecksums.TryGetValue(installerPath, out checksum!);
        }
    }

    private bool IsVerifiedStagingDirectory(string stagingDir)
    {
        lock (_verifiedArtifactChecksums)
        {
            return _verifiedArtifactChecksums.Keys.Any(path =>
                string.Equals(Path.GetDirectoryName(path), stagingDir, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void FailInstall(string message, string? stagingPath = null)
    {
        SetInstallState(InstallState.Failed, error: message);
        Log.Error(message);
        InstallFailed?.Invoke(this, message);
        if (stagingPath is not null)
        {
            var stagingDir = Path.GetDirectoryName(stagingPath);
            if (stagingDir is not null)
            {
                CleanupStagingDirectory(stagingDir);
            }
        }
    }

    private void SetInstallState(InstallState state, int? exitCode = null, string? error = null)
    {
        InstallState = state;
        LastInstallerExitCode = exitCode;
        LastInstallError = error;
        InstallStateChanged?.Invoke(this, new InstallStateChangedEventArgs(state, exitCode, error));
    }

    private void CleanupStagingDirectory(string stagingDir)
    {
        try
        {
            if (UpdateArtifactSecurity.IsPathUnderRoot(stagingDir, _stagingRoot)
                && Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clean up update staging directory {Path}", stagingDir);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BrightSync");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

public enum InstallState
{
    Idle,
    Pending,
    Starting,
    ScriptStarted,
    InstallerCompleted,
    Failed
}

public sealed class InstallStateChangedEventArgs : EventArgs
{
    public InstallStateChangedEventArgs(InstallState state, int? exitCode = null, string? error = null)
    {
        State = state;
        ExitCode = exitCode;
        Error = error;
    }

    public InstallState State { get; }

    public int? ExitCode { get; }

    public string? Error { get; }
}
