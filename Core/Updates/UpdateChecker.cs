using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using BrightSync.Core.Config;
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
                    throw;
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
        _timer.Dispose();
    }

    private async Task CheckForUpdatesIfNeededAsync()
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_configManager.Config.LastUpdateCheckDate == today)
            {
                return;
            }

            var latestVersion = await GetLatestReleaseVersionAsync();
            _configManager.Config.LastUpdateCheckDate = today;
            _configManager.Save();

            var currentVersion = GetCurrentVersion();
            if (latestVersion is not null && currentVersion is not null && latestVersion > currentVersion)
            {
                OpenReleasesPage();
            }
        }
        catch
        {
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

    private static Version? GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+', 2)[0];

        var parsedInformationalVersion = TryParseVersion(informationalVersion);
        if (parsedInformationalVersion is not null)
        {
            return parsedInformationalVersion;
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var parsedFileVersion = TryParseVersion(fileVersion);
        if (parsedFileVersion is not null && parsedFileVersion != new Version(1, 0, 0, 0))
        {
            return parsedFileVersion;
        }

        var versionFilePath = Path.Combine(AppContext.BaseDirectory, "VERSION");
        if (File.Exists(versionFilePath))
        {
            return TryParseVersion(File.ReadAllText(versionFilePath).Trim());
        }

        return null;
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