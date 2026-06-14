using System.Runtime.InteropServices;
using System.Text.Json;
using BrightSync.Core.Config;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Updates;

public sealed class UpdateChecker : IDisposable
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/bberka/BrightSync/releases/latest";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly ConfigManager _configManager;
    private readonly Timer _timer;
    private bool _disposed;

    public UpdateChecker(ConfigManager configManager)
    {
        _configManager = configManager;
        _timer = new Timer(async void (_) =>
            {
                try
                {
                    if (_configManager.Config.AutoCheckUpdates)
                        await CheckForUpdatesIfNeededAsync();
                    else
                        Log.Debug("Background update check skipped (AutoCheckUpdates disabled)");
                }
                catch (Exception e)
                {
                    Log.Error(e, "Background update check failed");
                }
            }, null, Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public UpdateCheckResult? LastResult { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Debug("Disposing update checker");
        _timer.Dispose();
    }

    public event EventHandler<UpdateCheckResult>? UpdateAvailable;

    public void Start()
    {
        _ = CheckNowAsync();
        ScheduleNextMidnightCheck();
    }

    public async Task<UpdateCheckResult> CheckNowAsync(bool force = false)
    {
        return await CheckForUpdatesIfNeededAsync(force);
    }

    public async Task<GitHubRelease?> GetLatestReleaseInfoAsync()
    {
        try
        {
            using var response = await HttpClient.GetAsync(LatestReleaseApiUrl);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);

            var root = document.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagProperty))
                return null;

            var tagName = tagProperty.GetString() ?? string.Empty;
            var version = TryParseVersion(tagName);
            if (version is null)
                return null;

            var downloadUrl = GetInstallerDownloadUrl(root, RuntimeInformation.ProcessArchitecture);
            return new GitHubRelease(tagName, version, downloadUrl);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch latest release info");
            return null;
        }
    }

    internal static string GetInstallerDownloadUrl(JsonElement root, Architecture processArchitecture)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var releaseAssets = new List<GitHubReleaseAsset>();
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameProp))
            {
                continue;
            }

            var name = nameProp.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var downloadUrl = string.Empty;
            if (asset.TryGetProperty("browser_download_url", out var browserUrlProp))
            {
                downloadUrl = browserUrlProp.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(downloadUrl) && asset.TryGetProperty("url", out var apiUrlProp))
            {
                downloadUrl = apiUrlProp.GetString() ?? string.Empty;
            }

            releaseAssets.Add(new GitHubReleaseAsset(name, downloadUrl));
        }

        return SelectInstallerDownloadUrl(releaseAssets, processArchitecture);
    }

    internal static string SelectInstallerDownloadUrl(
        IReadOnlyList<GitHubReleaseAsset> assets,
        Architecture processArchitecture)
    {
        if (assets.Count == 0)
        {
            return string.Empty;
        }

        var architectureToken = GetArchitectureToken(processArchitecture);
        var installers = assets
            .Where(static asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(static asset => !string.IsNullOrWhiteSpace(asset.DownloadUrl))
            .ToArray();

        if (installers.Length == 0)
        {
            return string.Empty;
        }

        static bool IsSetupInstaller(GitHubReleaseAsset asset)
            => asset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase);

        static bool MatchesArchitecture(GitHubReleaseAsset asset, string architectureToken)
            => !string.IsNullOrWhiteSpace(architectureToken)
               && asset.Name.Contains(architectureToken, StringComparison.OrdinalIgnoreCase);

        return installers.FirstOrDefault(asset =>
                       IsSetupInstaller(asset) && MatchesArchitecture(asset, architectureToken))
                   ?.DownloadUrl
               ?? installers.FirstOrDefault(asset => MatchesArchitecture(asset, architectureToken))?.DownloadUrl
               ?? installers.FirstOrDefault(IsSetupInstaller)?.DownloadUrl
               ?? installers[0].DownloadUrl;
    }

    private static string GetArchitectureToken(Architecture processArchitecture)
    {
        return processArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => string.Empty
        };
    }

    private async Task<UpdateCheckResult> CheckForUpdatesIfNeededAsync(bool force = false)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (!force && _configManager.Config.LastUpdateCheckDate == today)
            {
                Log.Debug("Update check skipped because it already ran on {Date}", today);
                return LastResult = UpdateCheckResult.Skipped(today);
            }

            var latestVersion = await GetLatestReleaseVersionAsync();
            _configManager.Config.LastUpdateCheckDate = today;
            _configManager.Save();
            if (latestVersion is null)
            {
                Log.Warning("Update check completed but no parseable release version was found");
            }
            else
            {
                Log.Information("Latest available version detected: {LatestVersion}", latestVersion);
            }

            var currentVersion = AppVersionInfo.GetCurrentVersion();
            if (latestVersion is not null && currentVersion is not null && latestVersion != currentVersion)
            {
                Log.Information("New version available. CurrentVersion={CurrentVersion}, LatestVersion={LatestVersion}",
                    currentVersion, latestVersion);
                var result = LastResult = UpdateCheckResult.UpdateAvailable(currentVersion, latestVersion);
                UpdateAvailable?.Invoke(this, result);
                return result;
            }

            Log.Debug("No update required. CurrentVersion={CurrentVersion}, LatestVersion={LatestVersion}",
                currentVersion, latestVersion);
            return LastResult = latestVersion is null
                ? UpdateCheckResult.Unavailable(currentVersion)
                : UpdateCheckResult.UpToDate(currentVersion, latestVersion);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed but will be retried later");
            return LastResult = UpdateCheckResult.Failed(ex);
        }
        finally
        {
            ScheduleNextMidnightCheck();
        }
    }

    private void ScheduleNextMidnightCheck()
    {
        if (_disposed) return;

        var now = DateTime.Now;
        var nextMidnight = now.Date.AddDays(1);
        var dueTime = nextMidnight - now;
        if (dueTime < TimeSpan.Zero)
        {
            dueTime = TimeSpan.Zero;
        }

        _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        Log.Debug("Scheduled next update check in {DueTime}", dueTime);
    }

    private static async Task<Version?> GetLatestReleaseVersionAsync()
    {
        using var response = await HttpClient.GetAsync(LatestReleaseApiUrl);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);

        if (!document.RootElement.TryGetProperty("tag_name", out var tagProperty))
        {
            return null;
        }

        return TryParseVersion(tagProperty.GetString());
    }

    private static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        return Version.TryParse(normalized, out var version) ? version : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BrightSync");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

public sealed record GitHubRelease(string TagName, Version Version, string InstallerDownloadUrl);

internal sealed record GitHubReleaseAsset(string Name, string DownloadUrl);

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version? CurrentVersion = null,
    Version? LatestVersion = null,
    DateOnly? LastCheckedDate = null,
    Exception? Error = null)
{
    public static UpdateCheckResult Skipped(DateOnly date)
        => new(UpdateCheckStatus.SkippedAlreadyCheckedToday, LastCheckedDate: date);

    public static UpdateCheckResult UpdateAvailable(Version? currentVersion, Version latestVersion)
        => new(UpdateCheckStatus.UpdateAvailable, currentVersion, latestVersion);

    public static UpdateCheckResult UpToDate(Version? currentVersion, Version latestVersion)
        => new(UpdateCheckStatus.UpToDate, currentVersion, latestVersion);

    public static UpdateCheckResult Unavailable(Version? currentVersion)
        => new(UpdateCheckStatus.LatestVersionUnavailable, currentVersion);

    public static UpdateCheckResult Failed(Exception error)
        => new(UpdateCheckStatus.Failed, Error: error);
}

public enum UpdateCheckStatus
{
    SkippedAlreadyCheckedToday,
    UpdateAvailable,
    UpToDate,
    LatestVersionUnavailable,
    Failed
}