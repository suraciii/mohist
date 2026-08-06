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
    public async Task Verify_keeps_granted_scopes_null_when_x_oauth_scopes_header_absent()
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
    public async Task Verify_reads_granted_scopes_from_x_oauth_scopes_header()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponseWithScopes(
            """{"ok":true,"team_id":"T123","user_id":"U_BOT","app_id":"A9"}""",
            "chat:write,im:history,users:read,app_mentions:read"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackBotIdentityVerificationPortAdapter(new SlackApiTransport(http));

        var result = await adapter.VerifyAsync(new("xoxb-candidate"));

        Assert.True(result.Verified);
        Assert.NotNull(result.GrantedScopes);
        var granted = result.GrantedScopes!;
        Assert.Equal(
            new[] { "app_mentions:read", "chat:write", "im:history", "users:read" },
            granted.OrderBy(scope => scope, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Verify_trims_and_drops_empty_entries_in_x_oauth_scopes_header()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponseWithScopes(
            """{"ok":true,"team_id":"T123","user_id":"U_BOT","app_id":"A9"}""",
            " chat:write , , im:history ,, users:read "));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackBotIdentityVerificationPortAdapter(new SlackApiTransport(http));

        var result = await adapter.VerifyAsync(new("xoxb-candidate"));

        Assert.True(result.Verified);
        Assert.NotNull(result.GrantedScopes);
        var granted = result.GrantedScopes!;
        Assert.Equal(
            new[] { "chat:write", "im:history", "users:read" },
            granted.OrderBy(scope => scope, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Verify_ok_false_is_not_verified_and_carries_no_granted_scopes_even_with_header()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponseWithScopes(
            """{"ok":false,"error":"invalid_auth"}""",
            scopesHeader: "chat:write"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackBotIdentityVerificationPortAdapter(new SlackApiTransport(http));

        var result = await adapter.VerifyAsync(new("xoxb-candidate"));

        Assert.False(result.Verified);
        Assert.Equal("invalid_auth", result.ErrorClass);
        Assert.Null(result.GrantedScopes);
    }

    [Fact]
    public async Task Verify_unparseable_body_is_not_verified()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":""", Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackBotIdentityVerificationPortAdapter(new SlackApiTransport(http));

        var result = await adapter.VerifyAsync(new("xoxb-candidate"));

        Assert.False(result.Verified);
        Assert.Equal("unparseable_response", result.ErrorClass);
        Assert.Null(result.GrantedScopes);
    }

    // The adapter never substitutes team/app; it surfaces exactly what Slack confirmed so a
    // caller comparing to the expected enrollment / Agent App detects the mismatch itself.
    [Fact]
    public async Task Verify_surfaces_provider_confirmed_team_and_app_so_callers_can_detect_mismatch()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponseWithScopes(
            """{"ok":true,"team_id":"T_OTHER","user_id":"U_BOT","app_id":"A_UNEXPECTED"}""",
            scopesHeader: "chat:write,users:read"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackBotIdentityVerificationPortAdapter(new SlackApiTransport(http));

        var result = await adapter.VerifyAsync(new("xoxb-candidate"));

        Assert.True(result.Verified);
        Assert.Equal("T_OTHER", result.WorkspaceTeamId);
        Assert.Equal("A_UNEXPECTED", result.AppId);
        Assert.NotNull(result.GrantedScopes);
        var granted = result.GrantedScopes!;
        Assert.Equal(
            new[] { "chat:write", "users:read" },
            granted.OrderBy(scope => scope, StringComparer.Ordinal).ToArray());
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

    private static HttpResponseMessage JsonResponse(string json) => JsonResponseWithScopes(json, scopesHeader: null);

    private static HttpResponseMessage JsonResponseWithScopes(string json, string? scopesHeader)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (scopesHeader is not null)
            response.Headers.Add("x-oauth-scopes", scopesHeader);
        return response;
    }

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
