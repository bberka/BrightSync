using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Platform;
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
        _stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
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
        if (PlatformServices.IsConfigured && !PlatformServices.Current.Capabilities.CanSelfInstallUpdates)
        {
            Log.Information("Self-update service disabled: {Os} installs updates through the package manager",
                PlatformServices.Current.Capabilities.OsName);
            return;
        }

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
        var downloadSucceeded = false;
        try
        {
            if (!UpdateArtifactSecurity.IsAllowedInstallerUrl(release.InstallerDownloadUrl))
            {
                Log.Warning("Rejected update download URL for release {TagName}", release.TagName);
                return null;
            }

            if (UpdateArtifactSecurity.TryGetReleaseDownloadTag(release.InstallerDownloadUrl, out var installerTag)
                && !installerTag.Equals(release.TagName, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Rejected update {TagName}: installer URL belongs to release {InstallerTag}",
                    release.TagName,
                    installerTag);
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

                if (UpdateArtifactSecurity.TryGetReleaseDownloadTag(release.ChecksumManifestUrl, out var manifestTag)
                    && !manifestTag.Equals(release.TagName, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("Rejected update {TagName}: checksum manifest belongs to release {ManifestTag}",
                        release.TagName,
                        manifestTag);
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

            operationToken.ThrowIfCancellationRequested();

            lock (_verifiedArtifactChecksums)
            {
                _verifiedArtifactChecksums[installerPath] = actualChecksum;
            }

            progress?.Report(100);
            operationToken.ThrowIfCancellationRequested();
            Log.Information("Verified update downloaded to {Path} ({TotalBytes} bytes)", installerPath, totalRead);
            downloadSucceeded = true;
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
            if (stagingDir is not null && !downloadSucceeded)
            {
                RemoveVerifiedArtifacts(stagingDir);
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
            if (!IsSafeInstallerPath(fullPath))
            {
                FailInstall("Install rejected because the staged artifact path is invalid.", fullPath);
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
            || !IsSafeInstallerPath(installerPath))
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
            if (!cancellationToken.IsCancellationRequested)
            {
                InstallFailed?.Invoke(this, "Download failed");
            }
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            var stagingDir = Path.GetDirectoryName(installerPath);
            if (stagingDir is not null)
            {
                RemoveVerifiedArtifacts(stagingDir);
                CleanupStagingDirectory(stagingDir);
            }
            return;
        }

        ScheduleIdleInstall(installerPath);

        if (_config.Config.AutoInstallMode == AutoInstallMode.Instantly)
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
        if (HasReparsePoint(_stagingRoot))
        {
            throw new IOException("The update staging root cannot be a reparse point.");
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var directory = Path.Combine(_stagingRoot, $"BrightSync-update-{Guid.NewGuid():N}");
            if (Directory.Exists(directory))
            {
                continue;
            }

            Directory.CreateDirectory(directory);
            if (!IsSafeStagingDirectory(directory))
            {
                throw new IOException("The update staging directory is not a safe child of the staging root.");
            }

            return directory;
        }

        throw new IOException("Could not create a unique update staging directory.");
    }

    private string? GenerateInstallScript(string installerPath, string expectedChecksum, out string statusPath)
    {
        statusPath = string.Empty;
        try
        {
            var stagingDir = Path.GetDirectoryName(installerPath);
            if (stagingDir is null
                || !IsSafeStagingDirectory(stagingDir)
                || !IsSafeInstallerPath(installerPath))
            {
                return null;
            }

            statusPath = Path.Combine(stagingDir, InstallStatusFileName);
            var scriptPath = Path.Combine(stagingDir, InstallScriptFileName);
            if (HasExistingPathOrReparsePoint(statusPath) || HasExistingPathOrReparsePoint(scriptPath))
            {
                return null;
            }

            var script =
                $$"""
                param([int]$ProcessId, [string]$InstallerPath, [string]$ExpectedSha256, [string]$StatusPath)

                function Write-InstallStatus([string]$State, [string]$ExitCode, [string]$ErrorMessage) {
                  $safeError = ($ErrorMessage -replace '[\r\n]+', ' ')
                  @($State, $ExitCode, $safeError) | Set-Content -LiteralPath $StatusPath -Encoding UTF8
                }

                Write-InstallStatus 'script-started' '' ''
                try {
                  if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
                    Wait-Process -Id $ProcessId -Timeout 30 -ErrorAction SilentlyContinue | Out-Null
                    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
                      throw 'BrightSync did not exit before the installer handoff timeout.'
                    }
                  }
                  $installerItem = Get-Item -LiteralPath $InstallerPath -Force -ErrorAction Stop
                  if (($installerItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'The staged installer path is a reparse point.'
                  }
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
            using var scriptStream = new FileStream(
                scriptPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                4096,
                useAsync: false);
            using var scriptWriter = new StreamWriter(
                scriptStream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            scriptWriter.Write(script);
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
        if (!Directory.Exists(_stagingRoot) || HasReparsePoint(_stagingRoot))
        {
            return;
        }

        try
        {
            foreach (var stagingDir in Directory.EnumerateDirectories(_stagingRoot, "BrightSync-update-*"))
            {
                if (!IsSafeStagingDirectory(stagingDir))
                {
                    Log.Warning("Ignoring unsafe update staging directory {Path}", stagingDir);
                    continue;
                }

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

                if (state.Equals("installer-completed", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
                {
                    SetInstallState(InstallState.InstallerCompleted, exitCode);
                    InstallCompleted?.Invoke(this, EventArgs.Empty);
                    RemoveVerifiedArtifacts(stagingDir);
                    CleanupStagingDirectory(stagingDir);
                }
                else if (state.Equals("installer-completed", StringComparison.OrdinalIgnoreCase)
                         || state is "installer-failed" or "installer-error")
                {
                    var message = state.Equals("installer-completed", StringComparison.OrdinalIgnoreCase)
                        ? "The installer reported completion without a zero exit code."
                        : string.IsNullOrWhiteSpace(error)
                            ? "The installer did not complete successfully."
                            : error;
                    SetInstallState(InstallState.Failed, exitCode, message);
                    InstallFailed?.Invoke(this, message);
                    RemoveVerifiedArtifacts(stagingDir);
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
        checksum = string.Empty;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(installerPath);
        }
        catch
        {
            return false;
        }

        lock (_verifiedArtifactChecksums)
        {
            return _verifiedArtifactChecksums.TryGetValue(fullPath, out checksum!);
        }
    }

    private void RemoveVerifiedArtifacts(string stagingDir)
    {
        string fullStagingDir;
        try
        {
            fullStagingDir = Path.GetFullPath(stagingDir);
        }
        catch
        {
            return;
        }

        lock (_verifiedArtifactChecksums)
        {
            foreach (var path in _verifiedArtifactChecksums.Keys
                         .Where(path => string.Equals(
                             Path.GetDirectoryName(path),
                             fullStagingDir,
                             StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                _verifiedArtifactChecksums.Remove(path);
            }
        }
    }

    private bool IsSafeStagingDirectory(string stagingDir)
    {
        try
        {
            var fullPath = Path.GetFullPath(stagingDir);
            var parent = Path.GetDirectoryName(fullPath);
            var name = Path.GetFileName(fullPath);
            return string.Equals(parent, _stagingRoot, StringComparison.OrdinalIgnoreCase)
                   && name.StartsWith("BrightSync-update-", StringComparison.Ordinal)
                   && UpdateArtifactSecurity.IsPathUnderRoot(fullPath, _stagingRoot)
                   && Directory.Exists(fullPath)
                   && !HasReparsePoint(_stagingRoot)
                   && !HasReparsePoint(fullPath);
        }
        catch
        {
            return false;
        }
    }

    private bool IsSafeInstallerPath(string installerPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(installerPath);
            var stagingDir = Path.GetDirectoryName(fullPath);
            return stagingDir is not null
                   && string.Equals(Path.GetFileName(fullPath), InstallerFileName, StringComparison.Ordinal)
                   && IsSafeStagingDirectory(stagingDir)
                   && File.Exists(fullPath)
                   && !HasReparsePoint(fullPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool HasExistingPathOrReparsePoint(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                return true;
            }

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            return true;
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
                RemoveVerifiedArtifacts(stagingDir);
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
            if (IsSafeStagingDirectory(stagingDir))
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
