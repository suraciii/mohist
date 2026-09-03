using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure.Security.Secrets;
using Xunit;

namespace Mohist.Server.Tests.GitHub;

[Trait("level", "L0")]
public sealed class GitHubAppClientTests
{
    private static readonly string TestPrivateKeyPem = CreateTestPrivateKeyPem();

    [Fact]
    public async Task DiscoverInstallation_AcceptsNumericInstallationId()
    {
        var files = KeyFile();
        var handler = new ResponseHandler(
            (HttpStatusCode.OK, "{\"id\":123456789012345678}"),
            (HttpStatusCode.OK, "{\"token\":\"installation-token\",\"expires_at\":\"2030-01-01T00:00:00Z\"}"),
            (HttpStatusCode.OK, "{\"name\":\"hello-world\",\"node_id\":\"R_node\",\"owner\":{\"login\":\"octocat\"}}"));

        var result = await CreateClient(handler, files).DiscoverInstallationAsync("octocat", "hello-world");

        Assert.Equal("123456789012345678", result.InstallationId);
        Assert.Equal("R_node", result.RepositoryNodeId);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task DiscoverInstallation_MapsNotFoundToInstallGuidance()
    {
        var error = await Assert.ThrowsAsync<GitHubAppInstallationException>(() =>
            CreateClient(new ResponseHandler((HttpStatusCode.NotFound, "{}")), KeyFile())
                .DiscoverInstallationAsync("octocat", "hello-world"));

        Assert.Equal("github_app_installation_required", error.Code);
        Assert.Contains("installationUrl", error.Details!.GetType().GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task DiscoverInstallation_RejectsCredentialWithoutInstallUrl()
    {
        var handler = new ResponseHandler((HttpStatusCode.Unauthorized, "{}"));

        var error = await Assert.ThrowsAsync<GitHubAppInstallationException>(() =>
            CreateClient(handler, KeyFile()).DiscoverInstallationAsync("octocat", "hello-world"));

        Assert.Equal("github_app_credential_rejected", error.Code);
        Assert.Null(error.Details?.GetType().GetProperty("installationUrl"));
    }

    [Fact]
    public async Task CreateInstallationToken_MapsRemovedInstallationToInstallGuidance()
    {
        var error = await Assert.ThrowsAsync<GitHubAppInstallationException>(() =>
            CreateClient(new ResponseHandler((HttpStatusCode.NotFound, "{}")), KeyFile())
                .CreateInstallationTokenAsync("123"));

        Assert.Equal("github_app_installation_required", error.Code);
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Contains("removed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("installationUrl", error.Details!.GetType().GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task DiscoverInstallation_PropagatesRemovedInstallationFromTokenExchange()
    {
        var handler = new ResponseHandler(
            (HttpStatusCode.OK, "{\"id\":123}"),
            (HttpStatusCode.NotFound, "{}"));

        var error = await Assert.ThrowsAsync<GitHubAppInstallationException>(() =>
            CreateClient(handler, KeyFile()).DiscoverInstallationAsync("octocat", "hello-world"));

        Assert.Equal("github_app_installation_required", error.Code);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DiscoverInstallation_DistinguishesPermissionFromRateLimit()
    {
        var permissionHandler = new ResponseHandler((HttpStatusCode.Forbidden, "{}"));
        var permission = await Assert.ThrowsAsync<GitHubAppInstallationException>(() =>
            CreateClient(permissionHandler, KeyFile()).DiscoverInstallationAsync("octocat", "hello-world"));
        Assert.Equal("github_app_permission_denied", permission.Code);

        var rateHandler = new ResponseHandler((HttpStatusCode.Forbidden, "{}"));
        rateHandler.Headers.Add(("X-RateLimit-Remaining", "0"));
        var rate = await Assert.ThrowsAsync<GitHubAppInstallationException>(() =>
            CreateClient(rateHandler, KeyFile()).DiscoverInstallationAsync("octocat", "hello-world"));
        Assert.Equal("github_app_rate_limited", rate.Code);

        var remainingHandler = new ResponseHandler((HttpStatusCode.Forbidden, "{}"));
        remainingHandler.Headers.Add(("X-RateLimit-Remaining", "4999"));
        var remaining = await Assert.ThrowsAsync<GitHubAppInstallationException>(() =>
            CreateClient(remainingHandler, KeyFile()).DiscoverInstallationAsync("octocat", "hello-world"));
        Assert.Equal("github_app_permission_denied", remaining.Code);
    }

    [Fact]
    public async Task PrivateKeyMissingReturnsActionableError()
    {
        var files = KeyFile();
        files.FileAvailable = false;

        var error = await Assert.ThrowsAsync<GitHubAppInstallationException>(() =>
            CreateClient(new ResponseHandler((HttpStatusCode.OK, "{}")), files).DiscoverInstallationAsync("octocat", "hello-world"));

        Assert.Equal("github_app_private_key_missing", error.Code);
        Assert.Contains("owner-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivateKeyRejectsSymlinkAndPermissiveMode()
    {
        var files = KeyFile();
        files.IsSymlink = true;
        var symlink = await Assert.ThrowsAsync<GitHubAppInstallationException>(() =>
            CreateClient(new ResponseHandler((HttpStatusCode.OK, "{}")), files).DiscoverInstallationAsync("octocat", "hello-world"));
        Assert.Equal("github_app_private_key_symlink", symlink.Code);

        files.IsSymlink = false;
        files.Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        var mode = await Assert.ThrowsAsync<GitHubAppInstallationException>(() =>
            CreateClient(new ResponseHandler((HttpStatusCode.OK, "{}")), files).DiscoverInstallationAsync("octocat", "hello-world"));
        Assert.Equal("github_app_private_key_permissions", mode.Code);
    }

    private static string CreateTestPrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static FakeProtectedFile KeyFile() => new()
    {
        Contents = Encoding.UTF8.GetBytes(TestPrivateKeyPem),
        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
    };

    private static GitHubAppClient CreateClient(ResponseHandler handler, FakeProtectedFile files) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") },
            Options.Create(new GitHubAppOptions
            {
                AppId = 123,
                AppSlug = "mohist",
                PrivateKeyPath = "/protected/github-app.pem",
            }),
            files,
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    private sealed class FakeProtectedFile : ISecretKeyFileOperations
    {
        public byte[] Contents { get; set; } = [];
        public UnixFileMode Mode { get; set; }
        public bool IsSymlink { get; set; }
        public bool FileAvailable { get; set; } = true;
        public bool FileExists(string path) => FileAvailable;
        public bool IsReparsePoint(string path) => IsSymlink;
        public UnixFileMode GetUnixFileMode(string path) => Mode;
        public void SetUnixFileMode(string path, UnixFileMode mode) => Mode = mode;
        public void CreateDirectory(string path) { }
        public bool TryCreateExclusive(string path, byte[] bytes, UnixFileMode ownerOnlyMode) => false;
        public Task WriteAllBytesAtomicAsync(string path, byte[] bytes, UnixFileMode ownerOnlyMode, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default) => Task.FromResult(Contents);
    }

    private sealed class ResponseHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<(string Name, string Value)> Headers { get; } = [];
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = responses[Math.Min(_index++, responses.Length - 1)];
            var message = new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
            foreach (var (name, value) in Headers)
                message.Headers.TryAddWithoutValidation(name, value);
            return Task.FromResult(message);
        }
    }
}
