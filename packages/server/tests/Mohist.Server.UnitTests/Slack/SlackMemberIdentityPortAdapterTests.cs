using System.Net;
using System.Text;
using Mohist.Server.Infrastructure.Slack.Ports;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackMemberIdentityPortAdapterTests
{
    [Fact]
    public async Task LookupMember_regular_member_is_confirmed_with_provider_facts()
    {
        var adapter = NewAdapter(_ => JsonResponse("""
            {"ok":true,"user":{"id":"U123","team_id":"T123","deleted":false,"is_bot":false,
             "is_app_user":false,"is_restricted":false,"is_ultra_restricted":false,"is_stranger":false}}
            """));

        var result = await adapter.LookupMemberAsync(new SlackMemberIdentityRequest("xoxb-bot", "U123"));

        Assert.True(result.Confirmed);
        Assert.Equal("U123", result.UserId);
        Assert.Equal("T123", result.TeamId);
        Assert.False(result.Deleted);
        Assert.False(result.IsBot);
        Assert.False(result.IsAppUser);
        Assert.False(result.IsRestricted);
        Assert.False(result.IsUltraRestricted);
        Assert.False(result.IsStranger);
        Assert.Null(result.ErrorClass);
    }

    [Theory]
    [InlineData("deleted", true)]
    [InlineData("is_bot", true)]
    [InlineData("is_app_user", true)]
    [InlineData("is_restricted", true)]
    [InlineData("is_ultra_restricted", true)]
    [InlineData("is_stranger", true)]
    public async Task LookupMember_flags_are_confirmed_facts(string flag, bool expected)
    {
        var adapter = NewAdapter(_ => JsonResponse("""
            {"ok":true,"user":{"id":"U123","team_id":"T123","deleted":false,"is_bot":false,
             "is_app_user":false,"is_restricted":false,"is_ultra_restricted":false,"is_stranger":false,
             "__FLAG__":true}}
            """.Replace("__FLAG__", flag)));

        var result = await adapter.LookupMemberAsync(new SlackMemberIdentityRequest("xoxb-bot", "U123"));

        Assert.True(result.Confirmed);
        Assert.Equal(expected, flag switch
        {
            "deleted" => result.Deleted,
            "is_bot" => result.IsBot,
            "is_app_user" => result.IsAppUser,
            "is_restricted" => result.IsRestricted,
            "is_ultra_restricted" => result.IsUltraRestricted,
            _ => result.IsStranger,
        });
    }

    [Fact]
    public async Task LookupMember_posts_user_form_with_the_bot_token()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"ok":true,"user":{"id":"U123","team_id":"T123","deleted":false,"is_bot":false,
             "is_app_user":false,"is_restricted":false,"is_ultra_restricted":false}}
            """));
        var adapter = NewAdapter(handler);

        await adapter.LookupMemberAsync(new SlackMemberIdentityRequest("xoxb-bot", "U123"));

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, recorded.Method);
        Assert.EndsWith(SlackMemberIdentityPortAdapter.UsersInfoEndpoint, recorded.Uri, StringComparison.Ordinal);
        Assert.Equal("user=U123", recorded.Body);
        Assert.Equal("Bearer xoxb-bot", recorded.Authorization);
    }

    [Fact]
    public async Task LookupMember_ok_false_is_unconfirmed_with_the_slack_error_class()
    {
        var adapter = NewAdapter(_ => JsonResponse("""{"ok":false,"error":"user_not_found"}"""));

        var result = await adapter.LookupMemberAsync(new SlackMemberIdentityRequest("xoxb-bot", "U_MISSING"));

        Assert.False(result.Confirmed);
        Assert.Equal("user_not_found", result.ErrorClass);
    }

    [Fact]
    public async Task LookupMember_missing_required_fields_is_unconfirmed()
    {
        var adapter = NewAdapter(_ => JsonResponse("""
            {"ok":true,"user":{"id":"U123"}}
            """));

        var result = await adapter.LookupMemberAsync(new SlackMemberIdentityRequest("xoxb-bot", "U123"));

        Assert.False(result.Confirmed);
        Assert.Equal("invalid_identity_response", result.ErrorClass);
    }

    [Theory]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"user":"U123"}""")]
    public async Task LookupMember_missing_or_malformed_user_object_is_unconfirmed(string json)
    {
        var adapter = NewAdapter(_ => JsonResponse(json));

        var result = await adapter.LookupMemberAsync(new SlackMemberIdentityRequest("xoxb-bot", "U123"));

        Assert.False(result.Confirmed);
        Assert.Equal("invalid_identity_response", result.ErrorClass);
    }

    [Fact]
    public async Task LookupMember_unparseable_body_is_unconfirmed()
    {
        var adapter = NewAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":""", Encoding.UTF8, "application/json"),
        });

        var result = await adapter.LookupMemberAsync(new SlackMemberIdentityRequest("xoxb-bot", "U123"));

        Assert.False(result.Confirmed);
        Assert.Equal("unparseable_response", result.ErrorClass);
    }

    [Fact]
    public async Task LookupMember_transport_failure_is_unconfirmed()
    {
        var adapter = NewAdapter(_ => throw new HttpRequestException("connection refused"));

        var result = await adapter.LookupMemberAsync(new SlackMemberIdentityRequest("xoxb-bot", "U123"));

        Assert.False(result.Confirmed);
        Assert.Equal("transport_error", result.ErrorClass);
    }

    [Fact]
    public async Task LookupMember_http_error_status_is_unconfirmed()
    {
        var adapter = NewAdapter(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var result = await adapter.LookupMemberAsync(new SlackMemberIdentityRequest("xoxb-bot", "U123"));

        Assert.False(result.Confirmed);
        Assert.Equal("http_429", result.ErrorClass);
    }

    [Fact]
    public async Task LookupConversation_bot_member_is_confirmed()
    {
        var adapter = NewAdapter(_ => JsonResponse("""
            {"ok":true,"channel":{"id":"C123","is_channel":true,"is_member":true}}
            """));

        var result = await adapter.LookupConversationAsync(new SlackConversationMembershipRequest("xoxb-bot", "C123"));

        Assert.True(result.Confirmed);
        Assert.True(result.IsMember);
    }

    [Fact]
    public async Task LookupConversation_non_member_channel_is_confirmed_not_member()
    {
        var adapter = NewAdapter(_ => JsonResponse("""
            {"ok":true,"channel":{"id":"C123","is_channel":true,"is_member":false}}
            """));

        var result = await adapter.LookupConversationAsync(new SlackConversationMembershipRequest("xoxb-bot", "C123"));

        Assert.True(result.Confirmed);
        Assert.False(result.IsMember);
    }

    [Fact]
    public async Task LookupConversation_posts_channel_form_with_the_bot_token()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"ok":true,"channel":{"id":"C123","is_member":true}}
            """));
        var adapter = NewAdapter(handler);

        await adapter.LookupConversationAsync(new SlackConversationMembershipRequest("xoxb-bot", "C123"));

        var recorded = Assert.Single(handler.Requests);
        Assert.EndsWith(SlackMemberIdentityPortAdapter.ConversationsInfoEndpoint, recorded.Uri, StringComparison.Ordinal);
        Assert.Equal("channel=C123", recorded.Body);
        Assert.Equal("Bearer xoxb-bot", recorded.Authorization);
    }

    [Fact]
    public async Task LookupConversation_not_in_channel_rejection_is_unconfirmed_with_the_error_class()
    {
        var adapter = NewAdapter(_ => JsonResponse("""{"ok":false,"error":"not_in_channel"}"""));

        var result = await adapter.LookupConversationAsync(new SlackConversationMembershipRequest("xoxb-bot", "C123"));

        Assert.False(result.Confirmed);
        Assert.Equal("not_in_channel", result.ErrorClass);
    }

    [Fact]
    public async Task LookupConversation_missing_is_member_flag_is_unconfirmed()
    {
        var adapter = NewAdapter(_ => JsonResponse("""{"ok":true,"channel":{"id":"C123"}}"""));

        var result = await adapter.LookupConversationAsync(new SlackConversationMembershipRequest("xoxb-bot", "C123"));

        Assert.False(result.Confirmed);
        Assert.Equal("invalid_conversation_response", result.ErrorClass);
    }

    [Fact]
    public async Task LookupConversation_transport_failure_is_unconfirmed()
    {
        var adapter = NewAdapter(_ => throw new HttpRequestException("connection refused"));

        var result = await adapter.LookupConversationAsync(new SlackConversationMembershipRequest("xoxb-bot", "C123"));

        Assert.False(result.Confirmed);
        Assert.Equal("transport_error", result.ErrorClass);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public async Task LookupMember_rejects_missing_token_or_user(string token, string user)
    {
        var adapter = NewAdapter(_ => JsonResponse("""{"ok":true,"user":{}}"""));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.LookupMemberAsync(new SlackMemberIdentityRequest("xoxb-bot", user)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.LookupMemberAsync(new SlackMemberIdentityRequest(token, "U123")));
    }

    private static SlackMemberIdentityPortAdapter NewAdapter(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        return new SlackMemberIdentityPortAdapter(new SlackApiTransport(http));
    }

    private static SlackMemberIdentityPortAdapter NewAdapter(
        Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        NewAdapter(new StubHttpMessageHandler(responder));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                body,
                request.Headers.Authorization?.ToString()));
            return responder(request);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string Body, string? Authorization);
}
