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

            var downloadUrl = GetInstallerDownloadUrl(root);
            return new GitHubRelease(tagName, version, downloadUrl);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch latest release info");
            return null;
        }
    }

    private static string GetInstallerDownloadUrl(JsonElement root)
    {
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var nameProp) &&
                    asset.TryGetProperty("browser_download_url", out var urlProp))
                {
                    var name = nameProp.GetString();
                    if (name != null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return urlProp.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }

    private async Task<UpdateCheckResult> CheckForUpdatesIfNeededAsync(bool force = false)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (!force && _configManager.Config.LastUpdateCheckDate == today)
            {
                Log.Debug("Update check skipped because it already ran on {Date}", today);
                return UpdateCheckResult.Skipped(today);
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
                var result = UpdateCheckResult.UpdateAvailable(currentVersion, latestVersion);
                UpdateAvailable?.Invoke(this, result);
                return result;
            }

            Log.Debug("No update required. CurrentVersion={CurrentVersion}, LatestVersion={LatestVersion}",
                currentVersion, latestVersion);
            return latestVersion is null
                ? UpdateCheckResult.Unavailable(currentVersion)
                : UpdateCheckResult.UpToDate(currentVersion, latestVersion);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed but will be retried later");
            return UpdateCheckResult.Failed(ex);
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