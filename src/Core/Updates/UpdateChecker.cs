using System.Runtime.InteropServices;
using System.Text.Json;
using BrightSync.Core.Config;
using BrightSync.Platform;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Updates;

public sealed class UpdateChecker : IDisposable
{
    private const string DefaultAssetOsToken = "win";

    internal const string LatestReleaseApiUrl = "https://api.github.com/repos/bberka/BrightSync/releases/latest";

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
            using var response = await HttpClient.GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var json = await UpdateArtifactSecurity.ReadAtMostAsync(
                stream,
                UpdateArtifactSecurity.DefaultMaxManifestBytes,
                CancellationToken.None);
            using var document = JsonDocument.Parse(json);

            var root = document.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagProperty))
                return null;

            var tagName = tagProperty.GetString() ?? string.Empty;
            var version = TryParseVersion(tagName);
            if (version is null)
                return null;

            var releaseAssets = GetReleaseAssets(root);
            var installer = SelectInstallerAsset(releaseAssets, RuntimeInformation.ProcessArchitecture);
            var checksumManifest = releaseAssets.FirstOrDefault(asset =>
                asset.Name.Equals(UpdateArtifactSecurity.ChecksumManifestAssetName, StringComparison.OrdinalIgnoreCase));

            return new GitHubRelease(
                tagName,
                version,
                installer?.DownloadUrl ?? string.Empty,
                installer?.Name,
                checksumManifest?.DownloadUrl);
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

        var releaseAssets = GetReleaseAssets(root);

        return SelectInstallerAsset(releaseAssets, processArchitecture)?.DownloadUrl ?? string.Empty;
    }

    internal static string SelectInstallerDownloadUrl(
        IReadOnlyList<GitHubReleaseAsset> assets,
        Architecture processArchitecture)
    {
        if (assets.Count == 0)
        {
            return string.Empty;
        }

        return SelectInstallerAsset(assets, processArchitecture)?.DownloadUrl ?? string.Empty;
    }

    internal static GitHubReleaseAsset? SelectInstallerAsset(
        IReadOnlyList<GitHubReleaseAsset> assets,
        Architecture processArchitecture)
    {
        if (assets.Count == 0)
        {
            return null;
        }

        var architectureToken = GetArchitectureToken(processArchitecture);
        var installers = assets
            .Where(static asset => UpdateArtifactSecurity.IsSafeInstallerAssetName(asset.Name))
            .Where(static asset => asset.Name.StartsWith("BrightSync-Setup-", StringComparison.OrdinalIgnoreCase))
            .Where(static asset => !string.IsNullOrWhiteSpace(asset.DownloadUrl))
            .ToArray();

        if (installers.Length == 0)
        {
            return null;
        }

        var osToken = PlatformServices.IsConfigured
            ? PlatformServices.Current.UpdateInstaller?.AssetOsToken
            : DefaultAssetOsToken;
        if (osToken is null)
            return null;

        bool MatchesArchitecture(GitHubReleaseAsset asset, string architectureToken)
            => !string.IsNullOrWhiteSpace(architectureToken)
               && asset.Name.Contains($"-{osToken}-{architectureToken}.", StringComparison.OrdinalIgnoreCase);

        return installers.FirstOrDefault(asset => MatchesArchitecture(asset, architectureToken));
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
            if (IsStrictlyNewerVersion(currentVersion, latestVersion))
            {
                Log.Information("New version available. CurrentVersion={CurrentVersion}, LatestVersion={LatestVersion}",
                    currentVersion, latestVersion);
                var result = LastResult = UpdateCheckResult.UpdateAvailable(currentVersion, latestVersion!);
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
        var json = await UpdateArtifactSecurity.ReadAtMostAsync(
            stream,
            UpdateArtifactSecurity.DefaultMaxManifestBytes,
            CancellationToken.None);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("tag_name", out var tagProperty))
        {
            return null;
        }

        return TryParseVersion(tagProperty.GetString());
    }

    internal static bool IsStrictlyNewerVersion(Version? currentVersion, Version? latestVersion)
    {
        if (currentVersion is null || latestVersion is null)
        {
            return false;
        }

        var currentComponents = new[]
        {
            currentVersion.Major,
            currentVersion.Minor,
            Math.Max(currentVersion.Build, 0),
            Math.Max(currentVersion.Revision, 0)
        };
        var latestComponents = new[]
        {
            latestVersion.Major,
            latestVersion.Minor,
            Math.Max(latestVersion.Build, 0),
            Math.Max(latestVersion.Revision, 0)
        };

        for (var index = 0; index < currentComponents.Length; index++)
        {
            if (latestComponents[index] != currentComponents[index])
            {
                return latestComponents[index] > currentComponents[index];
            }
        }

        return false;
    }

    internal static Version? TryParseVersion(string? value)
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

        if (normalized.Contains('-') || normalized.Contains('+'))
        {
            return null;
        }

        var components = normalized.Split('.');
        if (components.Length is < 2 or > 3
            || components.Any(component => component.Length == 0 || !component.All(char.IsDigit)))
        {
            return null;
        }

        return Version.TryParse(normalized, out var version) ? version : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BrightSync");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static IReadOnlyList<GitHubReleaseAsset> GetReleaseAssets(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GitHubReleaseAsset>();
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

        return releaseAssets;
    }
}

public sealed record GitHubRelease(string TagName, Version Version, string InstallerDownloadUrl)
{
    public string? InstallerAssetName { get; init; }

    public string? ChecksumManifestUrl { get; init; }

    public string? InstallerSha256 { get; init; }

    public GitHubRelease(
        string tagName,
        Version version,
        string installerDownloadUrl,
        string? installerAssetName = null,
        string? checksumManifestUrl = null,
        string? installerSha256 = null)
        : this(tagName, version, installerDownloadUrl)
    {
        InstallerAssetName = installerAssetName;
        ChecksumManifestUrl = checksumManifestUrl;
        InstallerSha256 = installerSha256;
    }
}

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
