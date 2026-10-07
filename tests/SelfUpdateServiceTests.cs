using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using BrightSync.Core.Config;
using BrightSync.Core.Updates;

namespace BrightSync.Tests;

public sealed class SelfUpdateServiceTests
{
    private const string InstallerUrl =
        "https://github.com/bberka/BrightSync/releases/download/v1.2.3/BrightSync-Setup-v1.2.3-win-x64.exe";

    private const string ManifestUrl =
        "https://github.com/bberka/BrightSync/releases/download/v1.2.3/BrightSync-SHA256SUMS.txt";

    [Fact]
    public async Task DownloadUpdateAsync_rejects_an_untrusted_host_before_sending_a_request()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("request was not expected"));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);

        var release = CreateRelease("https://example.test/update.exe", installerSha256: ValidChecksum);

        var path = await service.DownloadUpdateAsync(release);

        Assert.Null(path);
        Assert.Equal(0, handler.RequestCount);
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_rejects_an_installer_name_that_does_not_match_the_url()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("request was not expected"));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);

        var release = new GitHubRelease(
            "v1.2.3",
            new Version(1, 2, 3),
            InstallerUrl,
            "BrightSync-Setup-v1.2.3-win-arm64.exe",
            null,
            ValidChecksum);

        var path = await service.DownloadUpdateAsync(release);

        Assert.Null(path);
        Assert.Equal(0, handler.RequestCount);
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_rejects_an_installer_url_from_a_different_release()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("request was not expected"));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);

        var release = new GitHubRelease(
            "v1.2.3",
            new Version(1, 2, 3),
            InstallerUrl.Replace("v1.2.3", "v1.2.4", StringComparison.Ordinal),
            "BrightSync-Setup-v1.2.3-win-x64.exe",
            null,
            ValidChecksum);

        var path = await service.DownloadUpdateAsync(release);

        Assert.Null(path);
        Assert.Equal(0, handler.RequestCount);
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_rejects_a_checksum_manifest_from_a_different_release()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("request was not expected"));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);

        var release = new GitHubRelease(
            "v1.2.3",
            new Version(1, 2, 3),
            InstallerUrl,
            "BrightSync-Setup-v1.2.3-win-x64.exe",
            ManifestUrl.Replace("v1.2.3", "v1.2.4", StringComparison.Ordinal));

        var path = await service.DownloadUpdateAsync(release);

        Assert.Null(path);
        Assert.Equal(0, handler.RequestCount);
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_fails_closed_when_no_expected_checksum_is_available()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("request was not expected"));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);

        var path = await service.DownloadUpdateAsync(CreateRelease(InstallerUrl, installerSha256: null));

        Assert.Null(path);
        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(GetStagingDirectories(root));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_honors_caller_cancellation()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("request was not expected"));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var path = await service.DownloadUpdateAsync(
            CreateRelease(InstallerUrl, ValidChecksum),
            progress: null,
            cancellationToken: cancellation.Token);

        Assert.Null(path);
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_caller_cancellation_cleans_up_after_staging_starts()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateResponse(new byte[] { 1 });
        });
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);
        using var cancellation = new CancellationTokenSource();

        var download = service.DownloadUpdateAsync(
            CreateRelease(InstallerUrl, ValidChecksum),
            progress: null,
            cancellationToken: cancellation.Token);
        await requestStarted.Task;
        cancellation.Cancel();

        Assert.Null(await download);
        Assert.Empty(GetStagingDirectories(root));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_does_not_report_caller_cancellation_as_a_failure()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateResponse(new byte[] { 1 });
        });
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);
        using var cancellation = new CancellationTokenSource();
        var failedMessages = new List<string>();
        service.InstallFailed += (_, message) => failedMessages.Add(message);

        var install = service.DownloadAndInstallAsync(
            CreateRelease(InstallerUrl, ValidChecksum),
            progress: null,
            cancellationToken: cancellation.Token);
        await requestStarted.Task;
        cancellation.Cancel();

        await install;

        Assert.Empty(failedMessages);
        Assert.Empty(GetStagingDirectories(root));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_rejects_a_response_over_the_configured_size_limit_and_cleans_up()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var handler = new FakeHttpMessageHandler(_ => CreateResponse(payload));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 3);

        var path = await service.DownloadUpdateAsync(CreateRelease(InstallerUrl, Hash(payload)));

        Assert.Null(path);
        Assert.Empty(GetStagingDirectories(root));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_rejects_a_redirect_to_an_untrusted_host()
    {
        var handler = new FakeHttpMessageHandler(_ => CreateRedirectResponse("https://evil.example/update.exe"));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);

        var path = await service.DownloadUpdateAsync(CreateRelease(InstallerUrl, ValidChecksum));

        Assert.Null(path);
        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(GetStagingDirectories(root));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_enforces_the_redirect_limit()
    {
        const string redirectUrl =
            "https://release-assets.githubusercontent.com/github-production-release-asset/123/abc?sig=1";
        var handler = new FakeHttpMessageHandler(_ => CreateRedirectResponse(redirectUrl));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);

        var path = await service.DownloadUpdateAsync(CreateRelease(InstallerUrl, ValidChecksum));

        Assert.Null(path);
        Assert.Equal(UpdateArtifactSecurity.MaxRedirects + 1, handler.RequestCount);
        Assert.Empty(GetStagingDirectories(root));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_honors_the_operation_timeout()
    {
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateResponse(new byte[] { 1 });
        });
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(
            client,
            root,
            downloadTimeout: TimeSpan.FromMilliseconds(50),
            maxDownloadBytes: 1024);

        var path = await service.DownloadUpdateAsync(CreateRelease(InstallerUrl, ValidChecksum));

        Assert.Null(path);
        Assert.Empty(GetStagingDirectories(root));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_rejects_a_checksum_mismatch_and_cleans_up()
    {
        var payload = new byte[] { 9, 8, 7, 6 };
        var handler = new FakeHttpMessageHandler(_ => CreateResponse(payload));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);

        var path = await service.DownloadUpdateAsync(CreateRelease(InstallerUrl, ValidChecksum));

        Assert.Null(path);
        Assert.Empty(GetStagingDirectories(root));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_requires_and_reads_the_release_checksum_manifest()
    {
        var payload = new byte[] { 4, 5, 6, 7 };
        var checksum = Hash(payload);
        var manifest = $"{checksum}  BrightSync-Setup-v1.2.3-win-x64.exe\n";
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                ? CreateResponse(manifest)
                : CreateResponse(payload));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);

        var path = await service.DownloadUpdateAsync(new GitHubRelease(
            "v1.2.3",
            new Version(1, 2, 3),
            InstallerUrl,
            "BrightSync-Setup-v1.2.3-win-x64.exe",
            ManifestUrl));

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(2, handler.RequestCount);
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_uses_a_unique_staging_directory_per_install()
    {
        var payload = new byte[] { 1, 3, 3, 7 };
        var handler = new FakeHttpMessageHandler(_ => CreateResponse(payload));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);
        var release = CreateRelease(InstallerUrl, Hash(payload));

        var firstPath = await service.DownloadUpdateAsync(release);
        var secondPath = await service.DownloadUpdateAsync(release);

        Assert.NotNull(firstPath);
        Assert.NotNull(secondPath);
        Assert.NotEqual(firstPath, secondPath);
        Assert.NotEqual(Path.GetDirectoryName(firstPath), Path.GetDirectoryName(secondPath));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadUpdateAsync_cleans_up_when_final_progress_reporting_fails()
    {
        var payload = new byte[] { 8, 6, 7, 5, 3, 0, 9 };
        var handler = new FakeHttpMessageHandler(_ => CreateResponse(payload));
        var root = CreateTestRoot();
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);

        var path = await service.DownloadUpdateAsync(
            CreateRelease(InstallerUrl, Hash(payload)),
            new ThrowOnCompletionProgress());

        Assert.Null(path);
        Assert.Empty(GetStagingDirectories(root));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task InstallNow_reports_script_started_only_after_process_start_and_defers_completion()
    {
        var payload = new byte[] { 2, 4, 6, 8 };
        var root = CreateTestRoot();
        var handler = new FakeHttpMessageHandler(_ => CreateResponse(payload));
        var processStarts = new List<ProcessStartInfo>();
        var states = new List<InstallState>();
        var startedCount = 0;
        var completedCount = 0;
        var failedCount = 0;
        using var client = new HttpClient(handler);
        using var service = CreateService(
            client,
            root,
            processStarter: startInfo =>
            {
                processStarts.Add(startInfo);
                return new Process();
            });
        service.InstallStateChanged += (_, args) => states.Add(args.State);
        service.InstallStarted += (_, _) => startedCount++;
        service.InstallCompleted += (_, _) => completedCount++;
        service.InstallFailed += (_, _) => failedCount++;

        var path = await service.DownloadUpdateAsync(CreateRelease(InstallerUrl, Hash(payload)));
        Assert.NotNull(path);
        service.ScheduleIdleInstall(path!);
        service.InstallNow();

        Assert.Equal(InstallState.ScriptStarted, service.InstallState);
        Assert.Equal(new[] { InstallState.Pending, InstallState.Starting, InstallState.ScriptStarted }, states);
        Assert.Single(processStarts);
        Assert.Equal(1, startedCount);
        Assert.Equal(0, completedCount);
        Assert.Equal(0, failedCount);
        var script = File.ReadAllText(processStarts[0].ArgumentList[6]);
        Assert.Contains("Get-FileHash", script);
        Assert.Contains("FileAttributes]::ReparsePoint", script);
        Assert.Contains("installer handoff timeout", script);
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task InstallNow_reports_failure_when_the_handoff_process_cannot_start()
    {
        var payload = new byte[] { 5, 5, 5, 5 };
        var root = CreateTestRoot();
        var handler = new FakeHttpMessageHandler(_ => CreateResponse(payload));
        var failedMessages = new List<string>();
        using var client = new HttpClient(handler);
        using var service = CreateService(
            client,
            root,
            processStarter: _ => throw new InvalidOperationException("start failed"));
        service.InstallFailed += (_, message) => failedMessages.Add(message);

        var path = await service.DownloadUpdateAsync(CreateRelease(InstallerUrl, Hash(payload)));
        Assert.NotNull(path);
        service.ScheduleIdleInstall(path!);
        service.InstallNow();

        Assert.Equal(InstallState.Failed, service.InstallState);
        Assert.Single(failedMessages);
        Assert.Contains("start failed", failedMessages[0]);
        Assert.Empty(GetStagingDirectories(root));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task ScheduleIdleInstall_cleans_up_a_verified_artifact_that_disappeared()
    {
        var payload = new byte[] { 7, 7, 1, 1 };
        var root = CreateTestRoot();
        var handler = new FakeHttpMessageHandler(_ => CreateResponse(payload));
        using var client = new HttpClient(handler);
        using var service = CreateService(client, root, maxDownloadBytes: 1024);

        var path = await service.DownloadUpdateAsync(CreateRelease(InstallerUrl, Hash(payload)));
        Assert.NotNull(path);
        File.Delete(path!);

        service.ScheduleIdleInstall(path!);

        Assert.Equal(InstallState.Failed, service.InstallState);
        Assert.Empty(GetStagingDirectories(root));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_does_not_start_a_when_idle_install_immediately()
    {
        var payload = new byte[] { 3, 1, 4, 1, 5 };
        var root = CreateTestRoot();
        var handler = new FakeHttpMessageHandler(_ => CreateResponse(payload));
        var processStarts = new List<ProcessStartInfo>();
        var config = new ConfigManager();
        config.Config.AutoInstallMode = AutoInstallMode.WhenIdle;
        using var client = new HttpClient(handler);
        using var service = CreateService(
            client,
            root,
            config: config,
            processStarter: startInfo =>
            {
                processStarts.Add(startInfo);
                return new Process();
            });

        await service.DownloadAndInstallAsync(CreateRelease(InstallerUrl, Hash(payload)));

        Assert.True(service.IsInstallPending);
        Assert.Equal(InstallState.Pending, service.InstallState);
        Assert.Empty(processStarts);
        DeleteTestRoot(root);
    }

    [Fact]
    public void Start_reports_a_previous_installer_exit_status()
    {
        var root = CreateTestRoot();
        var stagingDir = Directory.CreateDirectory(Path.Combine(root, "BrightSync-update-previous"));
        File.WriteAllLines(Path.Combine(stagingDir.FullName, "install-status.txt"),
            new[] { "installer-completed", "0", string.Empty });
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => CreateResponse(Array.Empty<byte>())));
        using var service = CreateService(client, root);
        var completedCount = 0;
        service.InstallCompleted += (_, _) => completedCount++;

        service.Start();

        Assert.Equal(InstallState.InstallerCompleted, service.InstallState);
        Assert.Equal(0, service.LastInstallerExitCode);
        Assert.Equal(1, completedCount);
        Assert.False(Directory.Exists(stagingDir.FullName));
        DeleteTestRoot(root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    public void Start_does_not_report_install_completed_without_a_zero_exit_code(string exitCode)
    {
        var root = CreateTestRoot();
        var stagingDir = Directory.CreateDirectory(Path.Combine(root, "BrightSync-update-invalid-status"));
        File.WriteAllLines(Path.Combine(stagingDir.FullName, "install-status.txt"),
            new[] { "installer-completed", exitCode, string.Empty });
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => CreateResponse(Array.Empty<byte>())));
        using var service = CreateService(client, root);
        var completedCount = 0;
        var failedMessages = new List<string>();
        service.InstallCompleted += (_, _) => completedCount++;
        service.InstallFailed += (_, message) => failedMessages.Add(message);

        service.Start();

        Assert.Equal(InstallState.Failed, service.InstallState);
        Assert.Equal(exitCode == "" ? null : 1, service.LastInstallerExitCode);
        Assert.Equal(0, completedCount);
        Assert.Single(failedMessages);
        Assert.False(Directory.Exists(stagingDir.FullName));
        DeleteTestRoot(root);
    }

    private static SelfUpdateService CreateService(
        HttpClient client,
        string stagingRoot,
        TimeSpan? downloadTimeout = null,
        long maxDownloadBytes = 128 * 1024 * 1024,
        Func<ProcessStartInfo, Process?>? processStarter = null,
        ConfigManager? config = null)
    {
        return new SelfUpdateService(
            null,
            null,
            config ?? new ConfigManager(),
            client,
            processStarter ?? (_ => new Process()),
            _ => { },
            _ => { },
            stagingRoot,
            downloadTimeout ?? TimeSpan.FromSeconds(5),
            maxDownloadBytes,
            64 * 1024);
    }

    private static GitHubRelease CreateRelease(string url, string? installerSha256)
    {
        return new GitHubRelease(
            "v1.2.3",
            new Version(1, 2, 3),
            url,
            "BrightSync-Setup-v1.2.3-win-x64.exe",
            null,
            installerSha256);
    }

    private static string CreateTestRoot()
    {
        return Path.Combine(Path.GetTempPath(), "BrightSync-update-tests", Guid.NewGuid().ToString("N"));
    }

    private static string[] GetStagingDirectories(string root)
    {
        return Directory.Exists(root)
            ? Directory.GetDirectories(root, "BrightSync-update-*")
            : Array.Empty<string>();
    }

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(byte[] payload)
    {
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static HttpResponseMessage CreateResponse(byte[] payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        };
    }

    private static HttpResponseMessage CreateResponse(string content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "text/plain")
        };
    }

    private static HttpResponseMessage CreateRedirectResponse(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private const string ValidChecksum =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
            : this((request, _) => Task.FromResult(send(request)))
        {
        }

        public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return _send(request, cancellationToken);
        }
    }

    private sealed class ThrowOnCompletionProgress : IProgress<int>
    {
        public void Report(int value)
        {
            if (value == 100)
            {
                throw new InvalidOperationException("completion callback failed");
            }
        }
    }
}
