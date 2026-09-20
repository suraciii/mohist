using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Slack;

/// <summary>
/// The channel-thread read route: the Session in the query selects the bound
/// Connection, channel, and thread, and no caller-supplied channel or token
/// reaches Slack. Failure cases keep their own status and code so the CLI and
/// the agent can tell a missing binding from a missing permission.
/// </summary>
[Collection("SlackApiSurface")]
[Trait("level", "L1")]
public sealed class SlackThreadReadRouteSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackThreadReadRouteSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    private SlackApiTestScript SlackApi =>
        _fixture.Services.GetRequiredService<SlackApiTestScript>();

    public ValueTask InitializeAsync()
    {
        SlackApi.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SlackApi.Clear();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Read_returns_the_bound_thread_and_carries_the_continuation_back_to_slack()
    {
        var (connection, sessionId) = await LaunchChannelRootAsync("C-thread-1", "1712000000.000100");
        var replies = new Queue<string>([
            """{"ok":true,"messages":[{"type":"message","ts":"1712000000.000100","user":"U_HUMAN","text":"the deploy failed"},{"type":"message","ts":"1712000000.000200","user":"U_HUMAN","text":"again"}],"has_more":true,"response_metadata":{"next_cursor":"cursor-2"}}""",
            """{"ok":true,"messages":[{"type":"message","ts":"1712000000.000300","user":"U_HUMAN","text":"retrying now"}],"has_more":false,"response_metadata":{"next_cursor":""}}""",
        ]);
        SlackApi.Responder = request => request.RequestUri!.AbsolutePath.EndsWith("/conversations.replies")
            ? SlackApiTestScript.JsonResponse(replies.Dequeue())
            : SlackApiTestScript.JsonResponse("""{"ok":false,"error":"unexpected_slack_api_call"}""");

        using var first = await _fixture.Client.GetAsync(
            $"{ThreadPath(connection)}?sessionId={sessionId}&limit=2");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstData = firstDoc.RootElement.GetProperty("data");
        Assert.Equal(
            new[] { "C-thread-1", "1712000000.000100" },
            new[]
            {
                firstData.GetProperty("thread").GetProperty("channelId").GetString()!,
                firstData.GetProperty("thread").GetProperty("rootMessageId").GetString()!,
            });
        Assert.Equal(
            new[] { "1712000000.000100", "1712000000.000200" },
            firstData.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("ts").GetString()!).ToArray());
        var continuation = firstData.GetProperty("continuation").GetString();
        Assert.False(string.IsNullOrWhiteSpace(continuation));

        // The provider request proves the resolved credential, channel, and
        // bound root were used — none of them came from the request URL.
        var firstRequest = Assert.Single(SlackApi.Requests);
        Assert.Equal("Bearer xoxb", firstRequest.Authorization);
        Assert.Contains("channel=C-thread-1", firstRequest.Body, StringComparison.Ordinal);
        Assert.Contains("ts=1712000000.000100", firstRequest.Body, StringComparison.Ordinal);
        Assert.Contains("limit=2", firstRequest.Body, StringComparison.Ordinal);

        using var second = await _fixture.Client.GetAsync(
            $"{ThreadPath(connection)}?sessionId={sessionId}&limit=2&continuation={Uri.EscapeDataString(continuation!)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondData = secondDoc.RootElement.GetProperty("data");
        Assert.Equal(
            new[] { "1712000000.000300" },
            secondData.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("ts").GetString()!).ToArray());
        Assert.Equal(JsonValueKind.Null, secondData.GetProperty("continuation").ValueKind);

        var continuationRequest = SlackApi.Requests[1];
        Assert.Contains("cursor=cursor-2", continuationRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_creates_no_job_for_the_session()
    {
        var (connection, sessionId) = await LaunchChannelRootAsync("C-thread-2", "1712000100.000100");
        SlackApi.Responder = _ => SlackApiTestScript.JsonResponse(
            """{"ok":true,"messages":[{"type":"message","ts":"1712000100.000100","user":"U_HUMAN","text":"one"}],"has_more":false}""");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var before = await db.AgentJobs.CountAsync(job => job.AgentSessionId == sessionId);

        using var response = await _fixture.Client.GetAsync($"{ThreadPath(connection)}?sessionId={sessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await db.AgentJobs.CountAsync(job => job.AgentSessionId == sessionId));
    }

    [Fact]
    public async Task Read_refuses_a_session_that_belongs_to_another_project()
    {
        var (_, sessionId) = await LaunchChannelRootAsync("C-thread-3", "1712000200.000100");
        var otherProjectConnection = (await SlackManagedConnectionSeed.CreateAsync(_fixture)).Connection;

        using var response = await _fixture.Client.GetAsync(
            $"{ThreadPath(otherProjectConnection)}?sessionId={sessionId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("session_not_found", await ErrorCodeAsync(response));
        Assert.Empty(SlackApi.Requests);
    }

    [Fact]
    public async Task Read_refuses_an_unknown_session_without_asking_slack()
    {
        var (connection, _) = await LaunchChannelRootAsync("C-thread-4", "1712000300.000100");

        using var response = await _fixture.Client.GetAsync(
            $"{ThreadPath(connection)}?sessionId=session-unknown");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("session_not_found", await ErrorCodeAsync(response));
        Assert.Empty(SlackApi.Requests);
    }

    [Fact]
    public async Task Read_refuses_a_missing_session_id()
    {
        var (connection, _) = await LaunchChannelRootAsync("C-thread-5", "1712000400.000100");

        using var response = await _fixture.Client.GetAsync(ThreadPath(connection));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await ErrorCodeAsync(response));
        Assert.Empty(SlackApi.Requests);
    }

    [Fact]
    public async Task Read_refuses_a_continuation_from_another_session_without_asking_slack()
    {
        var seeded = await SlackManagedConnectionSeed.CreateAsync(_fixture);
        var connection = seeded.Connection;
        var sessionId = await LaunchChannelRootAsync(connection, "C-thread-6", "1712000500.000100");
        var otherSessionId = await LaunchChannelRootAsync(connection, "C-thread-6b", "1712000600.000100");
        SlackApi.Responder = _ => SlackApiTestScript.JsonResponse(
            """{"ok":true,"messages":[],"has_more":true,"response_metadata":{"next_cursor":"cursor-2"}}""");
        using var first = await _fixture.Client.GetAsync($"{ThreadPath(connection)}?sessionId={otherSessionId}");
        using var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var foreign = firstDoc.RootElement.GetProperty("data").GetProperty("continuation").GetString();
        SlackApi.Requests.Clear();

        using var response = await _fixture.Client.GetAsync(
            $"{ThreadPath(connection)}?sessionId={sessionId}&continuation={Uri.EscapeDataString(foreign!)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("continuation_invalid", await ErrorCodeAsync(response));
        Assert.Empty(SlackApi.Requests);
    }

    [Fact]
    public async Task Rate_limited_read_reports_the_retry_delay_without_retrying()
    {
        var (connection, sessionId) = await LaunchChannelRootAsync("C-thread-7", "1712000700.000100");
        SlackApi.Responder = _ =>
        {
            var tooMany = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent(
                    """{"ok":false,"error":"ratelimited"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
            tooMany.Headers.TryAddWithoutValidation("Retry-After", "33");
            return tooMany;
        };

        using var response = await _fixture.Client.GetAsync($"{ThreadPath(connection)}?sessionId={sessionId}");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rate_limited", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(33, doc.RootElement.GetProperty("details").GetProperty("retryAfterSeconds").GetInt32());
        Assert.Single(SlackApi.Requests);
    }

    [Fact]
    public async Task Provider_rejection_is_reported_as_a_bad_gateway_with_the_slack_error()
    {
        var (connection, sessionId) = await LaunchChannelRootAsync("C-thread-8", "1712000800.000100");
        SlackApi.Responder = _ => SlackApiTestScript.JsonResponse(
            """{"ok":false,"error":"not_in_channel"}""");

        using var response = await _fixture.Client.GetAsync($"{ThreadPath(connection)}?sessionId={sessionId}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("provider_rejected", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("not_in_channel", doc.RootElement.GetProperty("details").GetProperty("providerError").GetString());
    }

    [Fact]
    public async Task Read_refuses_a_limit_above_the_supported_maximum()
    {
        var (connection, sessionId) = await LaunchChannelRootAsync("C-thread-9", "1712000900.000100");

        using var response = await _fixture.Client.GetAsync(
            $"{ThreadPath(connection)}?sessionId={sessionId}&limit=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await ErrorCodeAsync(response));
        Assert.Empty(SlackApi.Requests);
    }

    private static string ThreadPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/thread";

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString();
    }

    private async Task<(AgentConnection Connection, string SessionId)> LaunchChannelRootAsync(
        string conversationId,
        string messageTs)
    {
        var seeded = await SlackManagedConnectionSeed.CreateAsync(_fixture);
        var sessionId = await LaunchChannelRootAsync(seeded.Connection, conversationId, messageTs, seeded.LeaseId);
        return (seeded.Connection, sessionId);
    }

    private async Task<string> LaunchChannelRootAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? leaseId = null)
    {
        leaseId ??= await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(
            _fixture, connection.ProjectId, connection.Id);
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress",
            new
            {
                apiAppId = "A123",
                isDirectMessage = false,
                teamId = connection.WorkspaceTeamId,
                conversationId,
                messageTs,
                threadTs = (string?)null,
                mentionedUserIds = new[] { connection.BotUserId },
                senderSlackUserId = "U_OWNER",
                senderKind = "human",
                text = "<@U123> look into the failure",
                leaseId,
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("sessionId").GetString()!;
    }
}
