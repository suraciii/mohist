using System.Net;
using System.Text;
using Mohist.Server.Infrastructure.Slack.Ports;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class SlackThreadQueryPortAdapterTests
{
    [Fact]
    public async Task ReadPage_posts_conversations_replies_with_channel_root_and_limit()
    {
        var handler = new StubHttpMessageHandler(_ => PageResponse("""[]""", hasMore: false));
        var adapter = NewAdapter(handler);

        await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb-bot", "C123", "1710.000100", null, 15));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://slack.test/api/conversations.replies", request.Uri);
        Assert.Equal("Bearer xoxb-bot", request.Authorization);
        Assert.Contains("channel=C123", request.Body, StringComparison.Ordinal);
        Assert.Contains("ts=1710.000100", request.Body, StringComparison.Ordinal);
        Assert.Contains("limit=15", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor=", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadPage_sends_the_continuation_as_the_provider_cursor()
    {
        var handler = new StubHttpMessageHandler(_ => PageResponse("""[]""", hasMore: false));
        var adapter = NewAdapter(handler);

        await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb-bot", "C123", "1710.000100", "dGVhbS1jdXJzb3I=", 5));

        var request = Assert.Single(handler.Requests);
        Assert.Contains("cursor=dGVhbS1jdXJzb3I%3D", request.Body, StringComparison.Ordinal);
        Assert.Contains("limit=5", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadPage_keeps_source_order_author_identity_and_string_timestamps()
    {
        var adapter = NewAdapter(_ => PageResponse("""
            [
              {"type":"message","ts":"1710.000100","user":"U_HUMAN","text":"ship it automatically"},
              {"type":"message","ts":"1710.000200","bot_id":"B_BOT","text":"automatic deploys are enabled"},
              {"type":"message","ts":"1710.000300","user":"U_HUMAN","text":"no, require manual approval"}
            ]
            """, hasMore: false));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.Equal(SlackThreadPageOutcome.Ok, page.Outcome);
        Assert.Equal(["1710.000100", "1710.000200", "1710.000300"], page.Messages.Select(message => message.Ts));
        Assert.Equal("U_HUMAN", page.Messages[0].AuthorUserId);
        Assert.Equal("B_BOT", page.Messages[1].AuthorBotId);
        Assert.Null(page.Messages[1].AuthorUserId);
        Assert.Equal("no, require manual approval", page.Messages[2].Text);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public async Task ReadPage_identifies_the_root_message_of_the_bound_thread()
    {
        var adapter = NewAdapter(_ => PageResponse("""
            [
              {"type":"message","ts":"1710.000100","user":"U1","text":"root"},
              {"type":"message","ts":"1710.000200","user":"U2","text":"reply"}
            ]
            """, hasMore: false));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.True(page.Messages[0].ThreadRoot);
        Assert.False(page.Messages[1].ThreadRoot);
    }

    [Fact]
    public async Task ReadPage_preserves_edit_deletion_and_unread_content_facts()
    {
        var adapter = NewAdapter(_ => PageResponse("""
            [
              {"type":"message","ts":"1710.000100","user":"U1","text":"proposal",
               "edited":{"user":"U1","ts":"1710.000150"}},
              {"type":"message","ts":"1710.000200","subtype":"message_deleted","text":"this message was deleted"},
              {"type":"message","ts":"1710.000300","user":"U2","text":"",
               "files":[{"id":"F1","name":"diagram.png"}]},
              {"type":"message","ts":"1710.000400","user":"U3","text":"",
               "blocks":[{"type":"image","image_url":"https://files.slack.test/x.png"}]},
              {"type":"message","ts":"1710.000500","user":"U4","text":"see the screenshot",
               "attachments":[{"id":1,"title":"screenshot"}]}
            ]
            """, hasMore: false));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.True(page.Messages[0].Edited);
        Assert.Equal("1710.000150", page.Messages[0].EditedTs);
        Assert.True(page.Messages[1].Deleted);
        Assert.Equal(string.Empty, page.Messages[2].Text);
        Assert.Equal(["file"], page.Messages[2].UnavailableContent);
        Assert.Equal(["rich_text"], page.Messages[3].UnavailableContent);
        Assert.Equal(["attachment"], page.Messages[4].UnavailableContent);
    }

    [Fact]
    public async Task ReadPage_keeps_a_block_message_with_text_represented_by_its_text()
    {
        var adapter = NewAdapter(_ => PageResponse("""
            [{"type":"message","ts":"1710.000100","user":"U1","text":"hello",
              "blocks":[{"type":"section","text":{"type":"mrkdwn","text":"hello"}}]}]
            """, hasMore: false));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.Empty(page.Messages[0].UnavailableContent);
    }

    [Fact]
    public async Task ReadPage_retains_a_provider_supplied_permalink_when_present()
    {
        var adapter = NewAdapter(_ => PageResponse("""
            [{"type":"message","ts":"1710.000100","user":"U1","text":"hello",
              "permalink":"https://example.slack.com/archives/C123/p1710000100"}]
            """, hasMore: false));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.Equal(
            "https://example.slack.com/archives/C123/p1710000100",
            page.Messages[0].Permalink);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""[{"type":"message","ts":"1710.000200","user":"U2","text":"late reply"}]""")]
    public async Task ReadPage_short_or_empty_page_with_a_continuation_is_not_completion(string messages)
    {
        var adapter = NewAdapter(_ => PageResponse(messages, hasMore: true, nextCursor: "next-page"));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.Equal(SlackThreadPageOutcome.Ok, page.Outcome);
        Assert.Equal("next-page", page.Continuation);
    }

    [Fact]
    public async Task ReadPage_completion_has_no_continuation()
    {
        var adapter = NewAdapter(_ => PageResponse("""[]""", hasMore: false, nextCursor: ""));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.Equal(SlackThreadPageOutcome.Ok, page.Outcome);
        Assert.Null(page.Continuation);
        Assert.Empty(page.Messages);
    }

    [Theory]
    [InlineData("""{"ok":true,"messages":[],"has_more":true}""")]
    [InlineData("""{"ok":true,"messages":[],"has_more":true,"response_metadata":{"next_cursor":""}}""")]
    [InlineData("""{"ok":true,"messages":[],"has_more":false,"response_metadata":{"next_cursor":"left-over"}}""")]
    [InlineData("""{"ok":true,"messages":[]}""")]
    [InlineData("""{"ok":true,"has_more":false}""")]
    public async Task ReadPage_contradictory_or_missing_pagination_metadata_fails_explicitly(string json)
    {
        var adapter = NewAdapter(_ => JsonResponse(json));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.Equal(SlackThreadPageOutcome.InvalidResponse, page.Outcome);
        Assert.Empty(page.Messages);
        Assert.Null(page.Continuation);
    }

    [Theory]
    [InlineData("""[{"type":"message","user":"U1","text":"no timestamp"}]""")]
    [InlineData("""["not-a-message"]""")]
    public async Task ReadPage_message_without_identity_fails_explicitly(string messages)
    {
        var adapter = NewAdapter(_ => PageResponse(messages, hasMore: false));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.Equal(SlackThreadPageOutcome.InvalidResponse, page.Outcome);
        Assert.Equal("invalid_message_response", page.ErrorClass);
    }

    [Fact]
    public async Task ReadPage_rate_limit_carries_the_retry_delay()
    {
        var adapter = NewAdapter(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", "42");
            return response;
        });

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.Equal(SlackThreadPageOutcome.RateLimited, page.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(42), page.RetryAfter);
        Assert.Empty(page.Messages);
    }

    [Fact]
    public async Task ReadPage_provider_rejection_keeps_the_slack_error_class()
    {
        var adapter = NewAdapter(_ => JsonResponse("""{"ok":false,"error":"not_in_channel"}"""));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.Equal(SlackThreadPageOutcome.ProviderRejected, page.Outcome);
        Assert.Equal("not_in_channel", page.ErrorClass);
    }

    [Fact]
    public async Task ReadPage_transport_failure_is_not_an_empty_history()
    {
        var adapter = NewAdapter(_ => throw new HttpRequestException("connection refused"));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.Equal(SlackThreadPageOutcome.TransportError, page.Outcome);
        Assert.Empty(page.Messages);
    }

    [Fact]
    public async Task ReadPage_unparseable_body_is_an_invalid_response()
    {
        var adapter = NewAdapter(_ => JsonResponse("""{"ok":"""));

        var page = await adapter.ReadPageAsync(new SlackThreadPageQuery("xoxb", "C123", "1710.000100", null, 15));

        Assert.Equal(SlackThreadPageOutcome.InvalidResponse, page.Outcome);
        Assert.Equal("unparseable_response", page.ErrorClass);
    }

    [Theory]
    [InlineData("", "C123", "1710.000100")]
    [InlineData("xoxb", "", "1710.000100")]
    [InlineData("xoxb", "C123", "")]
    public async Task ReadPage_rejects_missing_credential_or_thread_identity(
        string token,
        string conversationId,
        string threadRootTs)
    {
        var adapter = NewAdapter(_ => PageResponse("""[]""", hasMore: false));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.ReadPageAsync(new SlackThreadPageQuery(token, conversationId, threadRootTs, null, 15)));
    }

    private static SlackThreadQueryPortAdapter NewAdapter(StubHttpMessageHandler handler) =>
        new(new SlackApiTransport(new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") }));

    private static SlackThreadQueryPortAdapter NewAdapter(
        Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        NewAdapter(new StubHttpMessageHandler(responder));

    private static HttpResponseMessage PageResponse(string messages, bool hasMore, string nextCursor = "") =>
        JsonResponse(
            "{\"ok\":true,\"messages\":" + messages
            + ",\"has_more\":" + (hasMore ? "true" : "false")
            + ",\"response_metadata\":{\"next_cursor\":\"" + nextCursor + "\"}}");

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
                request.RequestUri?.ToString() ?? string.Empty,
                body,
                request.Headers.Authorization?.ToString()));
            return responder(request);
        }
    }

    private sealed record RecordedRequest(string Uri, string Body, string? Authorization);
}
