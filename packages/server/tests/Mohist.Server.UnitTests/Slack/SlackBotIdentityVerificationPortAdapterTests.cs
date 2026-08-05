using System.Net;
using System.Text;
using Mohist.Server.Infrastructure.Slack.Ports;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackBotIdentityVerificationPortAdapterTests
{
    [Fact]
    public async Task Verify_posts_auth_test_with_bot_token_and_returns_provider_confirmed_identity()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """{"ok":true,"team_id":"T123","user_id":"U_BOT","app_id":"A9","bot_id":"B1"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackBotIdentityVerificationPortAdapter(new SlackApiTransport(http));

        var result = await adapter.VerifyAsync(new("xoxb-candidate"));

        Assert.True(result.Verified);
        Assert.Equal("T123", result.WorkspaceTeamId);
        Assert.Equal("U_BOT", result.BotUserId);
        Assert.Equal("A9", result.AppId);
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("https://slack.test/api/auth.test", recorded.Uri);
        Assert.Equal("Bearer xoxb-candidate", recorded.Authorization);
    }

    [Fact]
    public async Task Verify_does_not_fabricate_scopes_slack_auth_test_does_not_expose()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """{"ok":true,"team_id":"T123","user_id":"U_BOT","app_id":"A9"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackBotIdentityVerificationPortAdapter(new SlackApiTransport(http));

        var result = await adapter.VerifyAsync(new("xoxb-candidate"));

        Assert.True(result.Verified);
        Assert.Null(result.GrantedScopes);
    }

    [Fact]
    public async Task Verify_slack_rejection_is_not_verified_with_error_class()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":false,"error":"invalid_auth"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackBotIdentityVerificationPortAdapter(new SlackApiTransport(http));

        var result = await adapter.VerifyAsync(new("xoxb-candidate"));

        Assert.False(result.Verified);
        Assert.Equal("invalid_auth", result.ErrorClass);
    }

    [Fact]
    public async Task Verify_missing_identity_fields_is_not_verified()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true,"team_id":"T123"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackBotIdentityVerificationPortAdapter(new SlackApiTransport(http));

        var result = await adapter.VerifyAsync(new("xoxb-candidate"));

        Assert.False(result.Verified);
        Assert.Equal("invalid_identity_response", result.ErrorClass);
    }

    [Fact]
    public async Task Verify_transport_failure_is_not_verified_with_transport_error_class()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackBotIdentityVerificationPortAdapter(new SlackApiTransport(http));

        var result = await adapter.VerifyAsync(new("xoxb-candidate"));

        Assert.False(result.Verified);
        Assert.Equal("transport_error", result.ErrorClass);
    }

    [Fact]
    public async Task Verify_rejects_blank_bot_token_without_network()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackBotIdentityVerificationPortAdapter(new SlackApiTransport(http));

        await Assert.ThrowsAsync<ArgumentException>(() => adapter.VerifyAsync(new(string.Empty)));
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new RecordedRequest(
                request.RequestUri?.ToString() ?? string.Empty,
                body,
                request.Headers.Authorization?.ToString()));
            return responder(request);
        }
    }

    private sealed record RecordedRequest(string Uri, string Body, string? Authorization);
}
