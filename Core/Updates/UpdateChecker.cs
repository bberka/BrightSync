using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using BrightSync.Core.Config;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Updates;

public sealed class UpdateChecker : IDisposable
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/bberka/BrightSync/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/bberka/BrightSync/releases";

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
                    await CheckForUpdatesIfNeededAsync();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Background update check failed");
                }
            }, null, Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        _ = CheckForUpdatesIfNeededAsync();
        ScheduleNextMidnightCheck();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Debug("Disposing update checker");
        _timer.Dispose();
    }

    private async Task CheckForUpdatesIfNeededAsync()
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_configManager.Config.LastUpdateCheckDate == today)
            {
                Log.Debug("Update check skipped because it already ran on {Date}", today);
                return;
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
            if (latestVersion is not null && currentVersion is not null && latestVersion > currentVersion)
            {
                Log.Information("New version available. CurrentVersion={CurrentVersion}, LatestVersion={LatestVersion}",
                    currentVersion, latestVersion);
                OpenReleasesPage();
            }
            else
            {
                Log.Debug("No update required. CurrentVersion={CurrentVersion}, LatestVersion={LatestVersion}",
                    currentVersion, latestVersion);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed but will be retried later");
            // Ignore network or parsing failures and try again on the next schedule/startup.
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

    private static void OpenReleasesPage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ReleasesPageUrl,
            UseShellExecute = true
        });
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BrightSync");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
