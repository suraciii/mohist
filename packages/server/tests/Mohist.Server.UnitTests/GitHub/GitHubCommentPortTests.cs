using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure.Security.Secrets;
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
        IntakeLabel = "mohist",
        FeedMode = GitHubFeedMode.Start,
        Approvers = [],
        Status = GitHubConnectionStatus.Active,
        IdentityKind = GitHubIdentityKind.Pat,
        CreatedAt = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero),
    };

    private static GitHubCommentPort CreatePort(FakeHttpMessageHandler handler)
    {
        var secrets = new FakeSecretStore();
        secrets.Set(GitHubConnectionStore.ApiSecretAddress("project-1", "conn-1"), "pat-1"u8.ToArray());
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") };
        return new GitHubCommentPort(http, null!, secrets, NullLogger<GitHubCommentPort>.Instance);
    }

    [Fact]
    public async Task FindDeliveryPullRequestUrlAsync_WithMatchingPull_ReturnsHtmlUrl()
    {
        var handler = new FakeHttpMessageHandler("""
            [
              { "number": 123, "head": { "ref": "mo/issue-7" }, "html_url": "https://github.com/octo/hello/pull/123" }
            ]
            """);
        var port = CreatePort(handler);

        var url = await port.FindDeliveryPullRequestUrlAsync(Connection(), issueNumber: 7, CancellationToken.None);

        Assert.Equal("https://github.com/octo/hello/pull/123", url);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/repos/octo/hello/pulls?head=octo:mo/issue-7&state=all", request.PathAndQuery);
    }

    [Fact]
    public async Task FindDeliveryPullRequestUrlAsync_NoPull_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler("[]");
        var port = CreatePort(handler);

        var url = await port.FindDeliveryPullRequestUrlAsync(Connection(), issueNumber: 7, CancellationToken.None);

        Assert.Null(url);
    }

    [Fact]
    public async Task FindDeliveryPullRequestUrlAsync_FailedResponse_Throws()
    {
        var handler = new FakeHttpMessageHandler("nope", HttpStatusCode.InternalServerError);
        var port = CreatePort(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            port.FindDeliveryPullRequestUrlAsync(Connection(), issueNumber: 7, CancellationToken.None));
    }

    private sealed class FakeHttpMessageHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public sealed record Request(HttpMethod Method, string PathAndQuery);

        public List<Request> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new Request(request.Method, request.RequestUri!.PathAndQuery));
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<SecretStoreAddress, byte[]> _secrets = [];

        public void Set(SecretStoreAddress address, byte[] value) => _secrets[address] = value;

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            _secrets[address] = plaintext;
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_secrets.TryGetValue(address, out var value) ? value : null);

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_secrets.Remove(address));

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
