using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Ports;
using Xunit;

namespace Mohist.Server.UnitTests.GitHub;

public sealed class GitHubCommentPortTests
{
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
