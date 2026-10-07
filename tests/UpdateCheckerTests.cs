using System.Runtime.InteropServices;
using System.Text.Json;
using BrightSync.Core.Updates;

namespace BrightSync.Tests;

public sealed class UpdateCheckerTests
{
    [Fact]
    public void IsStrictlyNewerVersion_only_accepts_a_newer_release()
    {
        Assert.True(UpdateChecker.IsStrictlyNewerVersion(new Version(1, 2, 3), new Version(1, 2, 4)));
        Assert.False(UpdateChecker.IsStrictlyNewerVersion(new Version(1, 2, 3), new Version(1, 2, 3)));
        Assert.False(UpdateChecker.IsStrictlyNewerVersion(new Version(1, 2, 3), new Version(1, 2, 2)));
        Assert.False(UpdateChecker.IsStrictlyNewerVersion(new Version(1, 2, 3, 0), new Version(1, 2, 3)));
        Assert.False(UpdateChecker.IsStrictlyNewerVersion(null, new Version(1, 2, 4)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("v1.2.3.4")]
    [InlineData("v1.2.3-beta")]
    public void TryParseVersion_rejects_malformed_release_tags(string? value)
    {
        Assert.Null(UpdateChecker.TryParseVersion(value));
    }

    [Fact]
    public void TryParseVersion_accepts_a_stable_release_tag()
    {
        Assert.Equal(new Version(1, 2, 3), UpdateChecker.TryParseVersion(" v1.2.3 "));
    }

    [Theory]
    [InlineData("https://github.com/bberka/BrightSync/releases/download/v1.2.3/BrightSync-Setup-v1.2.3-win-x64.exe", true)]
    [InlineData("https://api.github.com/repos/bberka/BrightSync/releases/assets/123", true)]
    [InlineData("http://github.com/bberka/BrightSync/releases/download/v1.2.3/BrightSync-Setup.exe", false)]
    [InlineData("https://example.test/bberka/BrightSync/releases/download/v1.2.3/BrightSync-Setup.exe", false)]
    [InlineData("https://github.com/bberka/Other/releases/download/v1.2.3/BrightSync-Setup.exe", false)]
    [InlineData("https://github.com/bberka/BrightSync/releases/latest/BrightSync-Setup.exe", false)]
    [InlineData("https://github.com/bberka/BrightSync/releases/download/v1.2.3/BrightSync-Setup-v1.2.3-win-x64%252f..exe", false)]
    public void IsAllowedInstallerUrl_enforces_https_host_and_release_path(string url, bool expected)
    {
        Assert.Equal(expected, UpdateArtifactSecurity.IsAllowedInstallerUrl(url));
    }

    [Theory]
    [InlineData("https://github.com/bberka/BrightSync/releases/download/v1.2.3/BrightSync-SHA256SUMS.txt", true)]
    [InlineData("https://api.github.com/repos/bberka/BrightSync/releases/assets/123", false)]
    [InlineData("https://github.com/bberka/BrightSync/releases/download/v1.2.3/BrightSync-other.txt", false)]
    public void IsAllowedChecksumManifestUrl_requires_the_published_manifest_asset(string url, bool expected)
    {
        Assert.Equal(expected, UpdateArtifactSecurity.IsAllowedChecksumManifestUrl(url));
    }

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/123/abc?sig=1", true)]
    [InlineData("https://release-assets.githubusercontent.com/not-a-release/123", false)]
    [InlineData("https://evil.example/github-production-release-asset/123/abc", false)]
    [InlineData("https://api.github.com/repos/bberka/BrightSync/releases/assets/123", false)]
    public void IsAllowedRedirectUrl_allows_only_github_release_cdn_paths(string url, bool expected)
    {
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri));
        Assert.Equal(expected, UpdateArtifactSecurity.IsAllowedRedirectUrl(uri));
    }

    [Fact]
    public void TryGetInstallerAssetName_rejects_a_name_that_does_not_match_the_download_url()
    {
        Assert.False(UpdateArtifactSecurity.TryGetInstallerAssetName(
            "BrightSync-Setup-v1.2.3-win-arm64.exe",
            "https://github.com/bberka/BrightSync/releases/download/v1.2.3/BrightSync-Setup-v1.2.3-win-x64.exe",
            out _));
    }

    [Fact]
    public void TryGetReleaseDownloadTag_reads_the_release_segment_without_accepting_api_urls()
    {
        Assert.True(UpdateArtifactSecurity.TryGetReleaseDownloadTag(
            "https://github.com/bberka/BrightSync/releases/download/v1.2.3/BrightSync-Setup-v1.2.3-win-x64.exe",
            out var tag));
        Assert.Equal("v1.2.3", tag);
        Assert.False(UpdateArtifactSecurity.TryGetReleaseDownloadTag(
            "https://api.github.com/repos/bberka/BrightSync/releases/assets/123",
            out _));
    }

    [Fact]
    public void IsPathUnderRoot_rejects_the_root_parent_and_sibling_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "BrightSync-update-root");

        Assert.True(UpdateArtifactSecurity.IsPathUnderRoot(Path.Combine(root, "child"), root));
        Assert.False(UpdateArtifactSecurity.IsPathUnderRoot(root, root));
        Assert.False(UpdateArtifactSecurity.IsPathUnderRoot(Path.Combine(root, "..", "outside"), root));
        Assert.False(UpdateArtifactSecurity.IsPathUnderRoot(root + "-sibling", root));
    }

    [Fact]
    public void TryReadSha256Manifest_returns_the_checksum_for_the_exact_asset()
    {
        const string checksum = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string assetName = "BrightSync-Setup-v1.2.3-win-x64.exe";

        var manifest = $"# BrightSync release checksums\n{checksum}  {assetName}\n";

        Assert.True(UpdateArtifactSecurity.TryReadSha256Manifest(manifest, assetName, out var actual));
        Assert.Equal(checksum.ToUpperInvariant(), actual);
    }

    [Fact]
    public void TryReadSha256Manifest_fails_when_the_expected_asset_is_missing()
    {
        const string checksum = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        Assert.False(UpdateArtifactSecurity.TryReadSha256Manifest(
            $"{checksum}  BrightSync-other.exe",
            "BrightSync-Setup-v1.2.3-win-x64.exe",
            out _));
    }

    [Fact]
    public async Task ReadAtMostAsync_rejects_a_response_over_the_limit()
    {
        await using var source = new MemoryStream(new byte[] { 1, 2, 3, 4 });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateArtifactSecurity.ReadAtMostAsync(source, 3, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAtMostAsync_handles_the_largest_supported_limit_without_overflow()
    {
        await using var source = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = await UpdateArtifactSecurity.ReadAtMostAsync(source, long.MaxValue, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, result);
    }

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
    public void SelectInstallerDownloadUrl_rejects_a_wrong_architecture_instead_of_falling_back()
    {
        var assets = new[]
        {
            new GitHubReleaseAsset("BrightSync-0.14.1-win-arm64.zip", "https://example.test/arm64.zip"),
            new GitHubReleaseAsset("BrightSync-Setup-v0.14.1-win-arm64.exe", "https://example.test/arm64-setup.exe")
        };

        var downloadUrl = UpdateChecker.SelectInstallerDownloadUrl(assets, Architecture.X64);

        Assert.Equal(string.Empty, downloadUrl);
    }

    [Fact]
    public void SelectInstallerDownloadUrl_rejects_a_non_setup_executable()
    {
        var assets = new[]
        {
            new GitHubReleaseAsset("BrightSync-helper-win-x64.exe", "https://example.test/helper.exe")
        };

        var downloadUrl = UpdateChecker.SelectInstallerDownloadUrl(assets, Architecture.X64);

        Assert.Equal(string.Empty, downloadUrl);
    }

    [Fact]
    public void GetInstallerDownloadUrl_uses_asset_api_url_when_browser_download_url_is_missing()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "assets": [
                {
                  "name": "BrightSync-Setup-v0.14.1-win-x64.exe",
                  "url": "https://api.github.com/repos/bberka/BrightSync/releases/assets/123"
                }
              ]
            }
            """);

        var downloadUrl = UpdateChecker.GetInstallerDownloadUrl(document.RootElement, Architecture.X64);

        Assert.Equal("https://api.github.com/repos/bberka/BrightSync/releases/assets/123", downloadUrl);
    }
}
