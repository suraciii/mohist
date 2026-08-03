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
    public async Task SlackIdentityResponsesMatchLiveSlackShapes()
    {
        var handler = new IdentityHttpHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var client = new SlackApiClient(http);

        var auth = await client.AuthTestAsync(BotToken, TestContext.Current.CancellationToken);
        var bot = await client.BotsInfoAsync("B123", BotToken, TestContext.Current.CancellationToken);

        Assert.True(auth.Ok);
        Assert.Null(auth.AppId);
        Assert.Equal("B123", auth.BotId);
        Assert.NotNull(auth.GrantedScopes);
        Assert.Equal(["chat:write", "im:history", "users:read"], auth.GrantedScopes.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("A123", bot.Bot?.AppId);
        var botRequest = Assert.Single(handler.Requests, request => request.Path.EndsWith("/bots.info", StringComparison.Ordinal));
        Assert.Equal("application/x-www-form-urlencoded", botRequest.ContentType);
        Assert.Equal("bot=B123", botRequest.Body);

        var user = await client.UsersInfoAsync("U123", BotToken, TestContext.Current.CancellationToken);

        Assert.True(user.Ok);
        Assert.Equal("U123", user.User?.Id);
        Assert.Equal("T123", user.User?.TeamId);
        Assert.False(user.User?.IsBot);
        Assert.False(user.User?.Deleted);
        var userRequest = Assert.Single(handler.Requests, request => request.Path.EndsWith("/users.info", StringComparison.Ordinal));
        Assert.Equal("application/x-www-form-urlencoded", userRequest.ContentType);
        Assert.Equal("user=U123", userRequest.Body);
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("transport unavailable");
    }

    private sealed class IdentityHttpHandler : HttpMessageHandler
    {
        public List<IdentityRecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new IdentityRecordedRequest(
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Content?.Headers.ContentType?.MediaType ?? string.Empty,
                requestBody));
            var responseBody = request.RequestUri?.AbsolutePath.EndsWith("/auth.test", StringComparison.Ordinal) == true
                ? "{\"ok\":true,\"team_id\":\"T123\",\"user_id\":\"U123\",\"bot_id\":\"B123\"}"
                : request.RequestUri?.AbsolutePath.EndsWith("/bots.info", StringComparison.Ordinal) == true
                    ? "{\"ok\":true,\"bot\":{\"id\":\"B123\",\"name\":\"Mohist\",\"app_id\":\"A123\"}}"
                    : "{\"ok\":true,\"user\":{\"id\":\"U123\",\"team_id\":\"T123\",\"is_bot\":false,\"deleted\":false,\"is_restricted\":false,\"is_ultra_restricted\":false,\"is_guest\":false}}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
            if (request.RequestUri?.AbsolutePath.EndsWith("/auth.test", StringComparison.Ordinal) == true)
                response.Headers.TryAddWithoutValidation("x-oauth-scopes", "chat:write,im:history,users:read");
            return response;
        }
    }

    private sealed record IdentityRecordedRequest(string Path, string ContentType, string Body);

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

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? BearerToken, string Body);
}
