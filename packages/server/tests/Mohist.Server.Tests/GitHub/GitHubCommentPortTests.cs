using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.GitHub;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.GitHub;

[Trait("level", "L0")]
public sealed class GitHubCommentPortTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);

    private static GitHubConnection Connection() => new()
    {
        Id = "conn-1",
        ProjectId = "project-1",
        Owner = "octo",
        Repo = "hello",
        RepositoryName = "hello-world",
        InstallationId = "installation-1",
        RepositoryNodeId = "repository-node-1",
        Status = GitHubConnectionStatus.Active,
    };

    private static GitHubCommentPort CreatePort(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") },
            new FakeTokenProvider(),
            connections: null,
            NullLogger<GitHubCommentPort>.Instance);

    [Fact]
    public async Task FindIssueByMarkerAsync_RequestsAllIssueStatesAndReturnsMarkerMatch()
    {
        const string marker = "<!-- mohist:mirror:link-1 -->";
        var handler = new FakeHttpMessageHandler($$"""
            [ { "number": 817, "body": "body\n\n{{marker}}" } ]
            """);
        var number = await CreatePort(handler).FindIssueByMarkerAsync(Connection(), marker);
        Assert.Equal(817, number);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/repos/octo/hello/issues?state=all&per_page=100&page=1", request.PathAndQuery);
        Assert.Equal("installation-token", request.Authorization);
    }

    [Fact]
    public async Task CreateIssueAsync_MalformedSuccessResponseIsUnknownOutcome()
    {
        var handler = new FakeHttpMessageHandler("{ \"title\": \"created\" }");
        var error = await Assert.ThrowsAsync<GitHubRemoteOutcomeUnknownException>(() =>
            CreatePort(handler).CreateIssueAsync(Connection(), "title", "body", "<!-- marker -->"));
        Assert.Contains("unusable response", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FindIssueByMarkerAsync_ReportsAmbiguousMarker()
    {
        const string marker = "<!-- mohist:mirror:link-1 -->";
        var handler = new FakeHttpMessageHandler($$"""
            [ { "number": 817, "body": "{{marker}}" }, { "number": 818, "body": "{{marker}}" } ]
            """);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePort(handler).FindIssueByMarkerAsync(Connection(), marker));
        Assert.Contains("multiple", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindCommentIdsByMarkerAsync_UsesInstallationToken()
    {
        const string marker = "<!-- mohist:command-reply:conn-1:42:1001 -->";
        var handler = new FakeHttpMessageHandler($$"""
            [ { "id": 123, "body": "reply\n\n{{marker}}" } ]
            """);
        var found = await CreatePort(handler).FindCommentIdsByMarkerAsync(Connection(), 42, marker);
        Assert.Equal(["123"], found);
        Assert.Equal("installation-token", Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task UnauthorizedResponseRefreshesTokenAndRetriesOnce()
    {
        var handler = new FakeHttpMessageHandler("{\"number\":42,\"title\":\"Issue\"}");
        handler.Statuses.Enqueue(HttpStatusCode.Unauthorized);
        handler.Statuses.Enqueue(HttpStatusCode.OK);
        var tokens = new RotatingTokenProvider();
        var port = new GitHubCommentPort(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") },
            tokens,
            connections: null,
            NullLogger<GitHubCommentPort>.Instance);

        var issue = await port.GetIssueAsync(Connection(), 42);

        Assert.Equal(42, issue!.Number);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("token-1", handler.Requests[0].Authorization);
        Assert.Equal("token-2", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task UnauthorizedRefreshFailure_401MarksConnectionUnavailable()
    {
        await AssertRefreshFailureMarksConnectionUnavailableAsync(HttpStatusCode.Unauthorized, "github_app_token_rejected");
    }

    [Fact]
    public async Task UnauthorizedRefreshPermissionFailure_403MarksConnectionUnavailable()
    {
        await AssertRefreshFailureMarksConnectionUnavailableAsync(HttpStatusCode.Forbidden, "github_app_permission_denied");
    }

    [Fact]
    public async Task TokenExchangeRemovedInstallation_MarksConnectionUnavailable()
    {
        using var database = NewConnectionDatabase();
        var handler = new FakeHttpMessageHandler("{}");
        var port = new GitHubCommentPort(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") },
            new InstallationMissingTokenProvider(),
            database.Store,
            NullLogger<GitHubCommentPort>.Instance);

        var error = await Assert.ThrowsAsync<GitHubAppInstallationException>(() => port.GetIssueAsync(Connection(), 42));
        var saved = await database.Store.GetAsync("project-1", "conn-1");

        Assert.Equal("github_app_installation_required", error.Code);
        Assert.Empty(handler.Requests);
        Assert.Equal(GitHubConnectionStatus.Disabled, saved!.Status);
        Assert.True(saved.ReconnectRequired);
        Assert.True(saved.NeedsAttention);
        Assert.Equal("github_app_installation_required", saved.LastErrorCode);
        Assert.Contains("installationUrl", saved.LastErrorDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindDeliveryPullRequestUrlAsync_WithMatchingPull_ReturnsHtmlUrl()
    {
        var handler = new FakeHttpMessageHandler("[{\"html_url\":\"https://github.com/octo/hello/pull/123\"}]");
        var url = await CreatePort(handler).FindDeliveryPullRequestUrlAsync(Connection(), 7);
        Assert.Equal("https://github.com/octo/hello/pull/123", url);
    }

    [Fact]
    public async Task FindDeliveryPullRequestUrlAsync_FailedResponse_Throws()
    {
        var handler = new FakeHttpMessageHandler("nope", HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<HttpRequestException>(() => CreatePort(handler).FindDeliveryPullRequestUrlAsync(Connection(), 7));
    }

    private static async Task AssertRefreshFailureMarksConnectionUnavailableAsync(
        HttpStatusCode failureStatus,
        string expectedCode)
    {
        using var database = NewConnectionDatabase();
        var handler = new FakeHttpMessageHandler("{}");
        handler.Statuses.Enqueue(HttpStatusCode.Unauthorized);
        var tokens = new RefreshFailureTokenProvider(failureStatus);
        var port = new GitHubCommentPort(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") },
            tokens,
            database.Store,
            NullLogger<GitHubCommentPort>.Instance);

        var error = await Assert.ThrowsAsync<GitHubRemoteRequestException>(() => port.GetIssueAsync(Connection(), 42));
        var saved = await database.Store.GetAsync("project-1", "conn-1");

        Assert.Equal(failureStatus, error.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Equal(2, tokens.RequestCount);
        Assert.Equal(GitHubConnectionStatus.Disabled, saved!.Status);
        Assert.True(saved.ReconnectRequired);
        Assert.True(saved.NeedsAttention);
        Assert.Equal(expectedCode, saved.LastErrorCode);
    }

    private sealed class ConnectionDatabase : IDisposable
    {
        private readonly SqliteConnection _keeper;
        public GitHubConnectionStore Store { get; }

        public ConnectionDatabase()
        {
            _keeper = new SqliteConnection("Data Source=:memory:");
            _keeper.Open();
            var options = new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(_keeper).Options;
            MigratedSqliteTemplate.CopyModelSchemaTo(_keeper);
            using (var db = new MohistDbContext(options))
            {
                db.GitHubConnections.Add(new GitHubConnectionRow
                {
                    Id = "conn-1",
                    ProjectId = "project-1",
                    Owner = "octo",
                    Repo = "hello",
                    RepositoryName = "hello-world",
                    ApproversJson = "[]",
                    Status = GitHubConnectionStatus.Active,
                    InstallationId = "installation-1",
                    RepositoryNodeId = "repository-node-1",
                    NeedsAttention = false,
                    CreatedAt = Now,
                    UpdatedAt = Now,
                });
                db.SaveChanges();
            }
            Store = new GitHubConnectionStore(new TestDbContextFactory(options), new FakeSecretStore(), new GitHubConnectionGate(), new FakeTimeProvider(Now));
        }

        public void Dispose() => _keeper.Dispose();
    }

    private static ConnectionDatabase NewConnectionDatabase() => new();

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options) : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(true);
        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }

    private sealed class RotatingTokenProvider : IGitHubInstallationTokenProvider
    {
        private int _version;
        public Task<GitHubInstallationToken> GetAsync(string installationId, CancellationToken ct = default) =>
            Task.FromResult(new GitHubInstallationToken($"token-{Volatile.Read(ref _version) + 1}", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        public void Invalidate(string installationId, string accessToken) => Interlocked.Increment(ref _version);
    }

    private sealed class FakeTokenProvider : IGitHubInstallationTokenProvider
    {
        public Task<GitHubInstallationToken> GetAsync(string installationId, CancellationToken ct = default) =>
            Task.FromResult(new GitHubInstallationToken("installation-token", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        public void Invalidate(string installationId, string accessToken)
        {
        }
    }

    private sealed class RefreshFailureTokenProvider(HttpStatusCode failureStatus) : IGitHubInstallationTokenProvider
    {
        public int RequestCount { get; private set; }

        public Task<GitHubInstallationToken> GetAsync(string installationId, CancellationToken ct = default)
        {
            RequestCount++;
            if (RequestCount == 1)
                return Task.FromResult(new GitHubInstallationToken("installation-token", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            throw new GitHubRemoteRequestException("token refresh failed", failureStatus);
        }

        public void Invalidate(string installationId, string accessToken)
        {
        }
    }

    private sealed class InstallationMissingTokenProvider : IGitHubInstallationTokenProvider
    {
        public Task<GitHubInstallationToken> GetAsync(string installationId, CancellationToken ct = default) =>
            throw new GitHubAppInstallationException(
                "The GitHub App installation is missing or was removed. Install the App again, then reconnect.",
                "github_app_installation_required",
                new { installationUrl = "https://github.com/apps/mohist/installations/new" },
                HttpStatusCode.NotFound);

        public void Invalidate(string installationId, string accessToken)
        {
        }
    }

    private sealed class FakeHttpMessageHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public sealed record Request(HttpMethod Method, string PathAndQuery, string? Authorization);
        public List<Request> Requests { get; } = [];
        public Queue<HttpStatusCode> Statuses { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Request(request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.Parameter));
            var responseStatus = Statuses.Count > 0 ? Statuses.Dequeue() : status;
            return Task.FromResult(new HttpResponseMessage(responseStatus)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
