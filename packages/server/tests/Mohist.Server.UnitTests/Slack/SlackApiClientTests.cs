using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mohist.Server.Slack;
using Xunit;

namespace Mohist.Server.Tests.Slack;

public sealed class SlackApiClientTests
{
    private const string BotToken = "xoxb-connection-token";

    [Fact]
    public async Task OpenFileContentAsyncReturnsDownloadedContentAndAuthoritativeMetadata()
    {
        var handler = new SlackFileHttpHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var client = new SlackApiClient(http);

        using var content = await client.OpenFileContentAsync("F123", BotToken, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(content.Stream, Encoding.UTF8);

        Assert.Equal("authoritative.txt", content.FileName);
        Assert.Equal("text/plain", content.ContentType);
        Assert.Equal(12, content.Size);
        Assert.Equal("file content", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Collection(handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://slack.test/api/files.info", request.Uri);
                Assert.Equal(BotToken, request.BearerToken);
                Assert.Contains("\"file\":\"F123\"", request.Body, StringComparison.Ordinal);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://files.slack.test/F123", request.Uri);
                Assert.Equal(BotToken, request.BearerToken);
                Assert.Equal(string.Empty, request.Body);
            });
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task OpenFileContentAsyncRaisesNotReadableWhenDownloadIsUnavailable(HttpStatusCode statusCode)
    {
        var handler = new SlackFileHttpHandler(statusCode);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var client = new SlackApiClient(http);

        await Assert.ThrowsAsync<SlackFileNotReadableException>(() =>
            client.OpenFileContentAsync("F123", BotToken, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenFileContentAsyncRaisesNotReadableOnTransportFailure()
    {
        using var http = new HttpClient(new ThrowingHttpHandler()) { BaseAddress = new Uri("https://slack.test/api/") };
        var client = new SlackApiClient(http);

        await Assert.ThrowsAsync<SlackFileNotReadableException>(() =>
            client.OpenFileContentAsync("F123", BotToken, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProviderMutationMethodsUseStableSlackIdentitiesAndClientMessageIds()
    {
        var handler = new SlackMutationHttpHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var client = new SlackApiClient(http);

        var posted = await client.ChatPostMessageAsync("C123", "Working...", "100.001", "status:1", BotToken);
        var updated = await client.ChatUpdateAsync("C123", posted.Ts!, "Completed", BotToken);
        var added = await client.ReactionsAddAsync("C123", "eyes", posted.Ts!, BotToken);
        var removed = await client.ReactionsRemoveAsync("C123", "eyes", posted.Ts!, BotToken);
        var reaction = await client.ReactionsGetAsync("C123", posted.Ts!, BotToken);
        var history = await client.ConversationsHistoryAsync("C123", posted.Ts, posted.Ts, null, BotToken);

        Assert.True(posted.Ok);
        Assert.True(updated.Ok);
        Assert.True(added.Ok);
        Assert.True(removed.Ok);
        Assert.Contains(reaction.Message!.Reactions!, value => value.Name == "eyes");
        Assert.Equal("status:1", history.Messages![0].ClientMessageId);
        Assert.Collection(handler.Requests,
            request => Assert.Contains("\"client_msg_id\":\"status:1\"", request.Body, StringComparison.Ordinal),
            request => Assert.Contains("\"ts\":\"100.002\"", request.Body, StringComparison.Ordinal),
            request => Assert.Contains("\"name\":\"eyes\"", request.Body, StringComparison.Ordinal),
            request => Assert.Contains("\"name\":\"eyes\"", request.Body, StringComparison.Ordinal),
            request => Assert.Contains("\"timestamp\":\"100.002\"", request.Body, StringComparison.Ordinal),
            request => Assert.Contains("\"latest\":\"100.002\"", request.Body, StringComparison.Ordinal));
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("transport unavailable");
    }

    private sealed class SlackFileHttpHandler(HttpStatusCode downloadStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.Authorization?.Scheme == "Bearer" ? request.Headers.Authorization.Parameter : null,
                body));

            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse("""
                    {"ok":true,"file":{"id":"F123","name":"authoritative.txt","mimetype":"text/plain","size":12,"url_private":"https://files.slack.test/F123"}}
                    """);
            }

            return new HttpResponseMessage(downloadStatus)
            {
                Content = new StringContent("file content", Encoding.UTF8, "text/plain"),
            };
        }

        private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class SlackMutationHttpHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.Authorization?.Scheme == "Bearer" ? request.Headers.Authorization.Parameter : null,
                body));
            var method = request.RequestUri?.AbsolutePath.Split('/').LastOrDefault();
            var response = method switch
            {
                "chat.postMessage" => "{\"ok\":true,\"ts\":\"100.002\"}",
                "chat.update" => "{\"ok\":true,\"ts\":\"100.002\"}",
                "reactions.add" or "reactions.remove" => "{\"ok\":true}",
                "reactions.get" => "{\"ok\":true,\"message\":{\"reactions\":[{\"name\":\"eyes\",\"users\":[\"U123\"],\"count\":1}]}}",
                "conversations.history" => "{\"ok\":true,\"messages\":[{\"ts\":\"100.002\",\"client_msg_id\":\"status:1\"}]}",
                _ => "{\"ok\":false,\"error\":\"unknown_method\"}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? BearerToken, string Body);
}
