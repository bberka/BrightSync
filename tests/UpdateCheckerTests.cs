using System.Runtime.InteropServices;
using System.Text.Json;
using BrightSync.Core.Updates;

namespace BrightSync.Tests;

public sealed class UpdateCheckerTests
{
    [Fact]
    public void SelectInstallerDownloadUrl_prefers_matching_setup_for_process_architecture()
    {
        var assets = new[]
        {
            new GitHubReleaseAsset("BrightSync-0.14.1-win-x64.zip", "https://example.test/x64.zip"),
            new GitHubReleaseAsset("BrightSync-Setup-v0.14.1-win-arm64.exe", "https://example.test/arm64-setup.exe"),
            new GitHubReleaseAsset("BrightSync-Setup-v0.14.1-win-x64.exe", "https://example.test/x64-setup.exe")
        };

        var downloadUrl = UpdateChecker.SelectInstallerDownloadUrl(assets, Architecture.X64);

        Assert.Equal("https://example.test/x64-setup.exe", downloadUrl);
    }

    [Fact]
    public void SelectInstallerDownloadUrl_falls_back_to_any_setup_when_architecture_specific_asset_is_missing()
    {
        var assets = new[]
        {
            new GitHubReleaseAsset("BrightSync-0.14.1-win-arm64.zip", "https://example.test/arm64.zip"),
            new GitHubReleaseAsset("BrightSync-Setup-v0.14.1-win-arm64.exe", "https://example.test/arm64-setup.exe")
        };

        var downloadUrl = UpdateChecker.SelectInstallerDownloadUrl(assets, Architecture.X64);

        Assert.Equal("https://example.test/arm64-setup.exe", downloadUrl);
    }

    [Fact]
    public void GetInstallerDownloadUrl_uses_asset_api_url_when_browser_download_url_is_missing()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "assets": [
                {
                  "name": "BrightSync-Setup-v0.14.1-x64.exe",
                  "url": "https://api.github.com/repos/bberka/BrightSync/releases/assets/123"
                }
              ]
            }
            """);

        var downloadUrl = UpdateChecker.GetInstallerDownloadUrl(document.RootElement, Architecture.X64);

        Assert.Equal("https://api.github.com/repos/bberka/BrightSync/releases/assets/123", downloadUrl);
    }
}