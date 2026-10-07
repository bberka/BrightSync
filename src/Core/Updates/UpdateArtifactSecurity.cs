namespace BrightSync.Core.Updates;

internal static class UpdateArtifactSecurity
{
    internal const string RepositoryOwner = "bberka";
    internal const string RepositoryName = "BrightSync";
    internal const string ChecksumManifestAssetName = "BrightSync-SHA256SUMS.txt";
    internal const long DefaultMaxInstallerBytes = 128L * 1024 * 1024;
    internal const long DefaultMaxManifestBytes = 64L * 1024;
    internal const int MaxRedirects = 3;
    internal static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(2);

    internal static bool IsAllowedInstallerUrl(string? value)
    {
        return TryParseHttpsUri(value, out var uri) && IsReleaseAssetUrl(uri, requireInstallerAsset: true);
    }

    internal static bool IsAllowedChecksumManifestUrl(string? value)
    {
        if (!TryParseHttpsUri(value, out var uri))
        {
            return false;
        }

        if (IsGitHubApiReleaseAssetUrl(uri))
        {
            return true;
        }

        if (!IsGitHubReleaseDownloadUrl(uri, out var assetName))
        {
            return false;
        }

        return assetName.Equals(ChecksumManifestAssetName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAllowedRedirectUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (IsReleaseAssetUrl(uri, requireInstallerAsset: false))
        {
            return true;
        }

        if (!uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryGetPathSegments(uri, out var segments)
               && segments.Count >= 2
               && segments[0].StartsWith("github-production-release-asset", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryGetInstallerAssetName(
        string? suppliedName,
        string downloadUrl,
        out string installerAssetName)
    {
        if (IsSafeInstallerAssetName(suppliedName))
        {
            installerAssetName = suppliedName!;
            return true;
        }

        if (!TryParseHttpsUri(downloadUrl, out var uri)
            || !IsGitHubReleaseDownloadUrl(uri, out var urlAssetName)
            || !IsSafeInstallerAssetName(urlAssetName))
        {
            installerAssetName = string.Empty;
            return false;
        }

        installerAssetName = urlAssetName;
        return true;
    }

    internal static bool IsValidSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryReadSha256Manifest(
        string manifest,
        string installerAssetName,
        out string checksum)
    {
        checksum = string.Empty;
        if (string.IsNullOrWhiteSpace(manifest) || !IsSafeInstallerAssetName(installerAssetName))
        {
            return false;
        }

        using var reader = new StringReader(manifest);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOfAny(new[] { ' ', '\t' });
            if (separator <= 0)
            {
                continue;
            }

            var candidateChecksum = trimmed[..separator].Trim();
            var candidateName = trimmed[(separator + 1)..].Trim();
            if (candidateName.StartsWith('*'))
            {
                candidateName = candidateName[1..];
            }

            if (candidateName.Equals(installerAssetName, StringComparison.Ordinal)
                && IsValidSha256(candidateChecksum))
            {
                checksum = candidateChecksum.ToUpperInvariant();
                return true;
            }
        }

        return false;
    }

    internal static async Task<byte[]> ReadAtMostAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        long totalRead = 0;

        while (true)
        {
            var requested = (int)Math.Min(buffer.Length, maximumBytes - totalRead + 1);
            if (requested <= 0)
            {
                throw new InvalidDataException($"Response exceeded the {maximumBytes}-byte limit.");
            }

            var bytesRead = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
            if (totalRead > maximumBytes)
            {
                throw new InvalidDataException($"Response exceeded the {maximumBytes}-byte limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        return destination.ToArray();
    }

    internal static bool IsPathUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var rootWithSeparator = fullRoot + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSafeInstallerAssetName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value is "." or ".." || value.Contains('/') || value.Contains('\\') || value.Contains(':'))
        {
            return false;
        }

        return value.All(character => !char.IsControl(character));
    }

    private static bool IsReleaseAssetUrl(Uri uri, bool requireInstallerAsset)
    {
        if (IsGitHubApiReleaseAssetUrl(uri))
        {
            return true;
        }

        if (!IsGitHubReleaseDownloadUrl(uri, out var assetName))
        {
            return false;
        }

        return !requireInstallerAsset || IsSafeInstallerAssetName(assetName);
    }

    private static bool IsGitHubApiReleaseAssetUrl(Uri uri)
    {
        if (!uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || !TryGetPathSegments(uri, out var segments)
            || segments.Count != 6)
        {
            return false;
        }

        return segments[0].Equals("repos", StringComparison.Ordinal)
               && segments[1].Equals(RepositoryOwner, StringComparison.Ordinal)
               && segments[2].Equals(RepositoryName, StringComparison.Ordinal)
               && segments[3].Equals("releases", StringComparison.Ordinal)
               && segments[4].Equals("assets", StringComparison.Ordinal)
               && segments[5].All(char.IsDigit);
    }

    private static bool IsGitHubReleaseDownloadUrl(Uri uri, out string assetName)
    {
        assetName = string.Empty;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !TryGetPathSegments(uri, out var segments)
            || segments.Count != 6)
        {
            return false;
        }

        if (!segments[0].Equals(RepositoryOwner, StringComparison.Ordinal)
            || !segments[1].Equals(RepositoryName, StringComparison.Ordinal)
            || !segments[2].Equals("releases", StringComparison.Ordinal)
            || !segments[3].Equals("download", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(segments[4])
            || !IsSafeReleasePathSegment(segments[4])
            || !IsSafeReleasePathSegment(segments[5]))
        {
            return false;
        }

        assetName = segments[5];
        return true;
    }

    private static bool TryParseHttpsUri(string? value, out Uri uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            uri = null!;
            return false;
        }

        return true;
    }

    private static bool TryGetPathSegments(Uri uri, out IReadOnlyList<string> segments)
    {
        var rawSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var decodedSegments = new List<string>(rawSegments.Length);
        foreach (var rawSegment in rawSegments)
        {
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(rawSegment);
            }
            catch (UriFormatException)
            {
                segments = Array.Empty<string>();
                return false;
            }

            if (!IsSafeReleasePathSegment(decoded))
            {
                segments = Array.Empty<string>();
                return false;
            }

            decodedSegments.Add(decoded);
        }

        segments = decodedSegments;
        return true;
    }

    private static bool IsSafeReleasePathSegment(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value is not "." and not ".."
               && !value.Contains('/')
               && !value.Contains('\\')
               && !value.Contains(':')
               && value.All(character => !char.IsControl(character));
    }
}
