using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack.Ports;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Live-gate branch of the access-policy spec (same subject and fixture as
/// <see cref="SlackAccessPolicySpecs"/>, split out to stay under the spec
/// file-size ratchet): under <c>allowlist</c> and <c>anyone</c> the decider
/// re-proves the runtime lease, resolves the verified Agent App Bot token
/// and calls <c>users.info</c> (<c>conversations.info</c> under
/// <c>anyone</c>) through the production adapter + transport against the
/// scripted fake Slack API. Every test resets the shared script first, so
/// ordering can never leak between tests in the serial collection.
/// </summary>
public sealed partial class SlackAccessPolicySpecs
{
    private const string ListedMember = "U_LISTED";

    [Fact]
    public async Task Allowlist_listed_member_root_mention_is_accepted_through_users_info()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Allowlist, [ListedMember]);
        SlackApi.Clear();
        SlackApi.Responder = request => ScriptedMember(request, RegularMemberJson(ListedMember));

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-live-allowlist-ok",
            messageTs: "1710000000.101000",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: ListedMember,
            text: "<@U123> run the report");

        Assert.Equal("accepted", data.GetProperty("kind").GetString());
        var recorded = Assert.Single(SlackApi.Requests);
        Assert.EndsWith(SlackMemberIdentityPortAdapter.UsersInfoEndpoint, recorded.Uri, StringComparison.Ordinal);
        // The live gate resolves the verified Agent App Bot token through
        // the runtime lease seam, never the legacy connection secret.
        Assert.Equal("Bearer xoxb-verified", recorded.Authorization);
        Assert.Contains("user=U_LISTED", recorded.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allowlist_unlisted_member_is_rejected_without_any_slack_call()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Allowlist, [ListedMember]);
        SlackApi.Clear();

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-live-allowlist-unlisted",
            messageTs: "1710000000.101010",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> let me in");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("allowlist", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(SlackApi.Requests);
    }

    [Fact]
    public async Task Allowlist_listed_member_who_was_deleted_is_rejected_with_no_resources()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Allowlist, [ListedMember]);
        SlackApi.Clear();
        SlackApi.Responder = request => ScriptedMember(request, MemberJson(ListedMember, "T123", deleted: true));

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-live-allowlist-stale",
            messageTs: "1710000000.101020",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: ListedMember,
            text: "<@U123> still here?");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("regular member", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-live-allowlist-stale")
            .ToListAsync());
    }

    [Fact]
    public async Task Allowlist_listed_member_with_unconfirmed_identity_is_rejected()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Allowlist, [ListedMember]);
        SlackApi.Clear();
        SlackApi.Responder = _ => SlackApiTestScript.JsonResponse("""{"ok":false,"error":"user_not_found"}""");

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-live-allowlist-unconfirmed",
            messageTs: "1710000000.101030",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: ListedMember,
            text: "<@U123> hello?");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("regular member", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anyone_workspace_member_in_a_bot_channel_is_accepted()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Anyone);
        SlackApi.Clear();
        SlackApi.Responder = request => ScriptedMember(request, RegularMemberJson("U_OTHER"), conversation: """
            {"ok":true,"channel":{"id":"C123","is_channel":true,"is_member":true}}
            """);

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-live-anyone-ok",
            messageTs: "1710000000.101100",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> delegate this");

        Assert.Equal("accepted", data.GetProperty("kind").GetString());
        Assert.Equal(2, SlackApi.Requests.Count);
        Assert.All(SlackApi.Requests, request => Assert.Equal("Bearer xoxb-verified", request.Authorization));
        Assert.Contains(SlackApi.Requests, request => request.Uri.EndsWith(SlackMemberIdentityPortAdapter.UsersInfoEndpoint, StringComparison.Ordinal));
        Assert.Contains(SlackApi.Requests, request => request.Uri.EndsWith(SlackMemberIdentityPortAdapter.ConversationsInfoEndpoint, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Anyone_workspace_member_where_bot_is_not_a_channel_member_is_rejected()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Anyone);
        SlackApi.Clear();
        SlackApi.Responder = request => ScriptedMember(request, RegularMemberJson("U_OTHER"), conversation: """
            {"ok":true,"channel":{"id":"C123","is_channel":true,"is_member":false}}
            """);

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-live-anyone-not-member",
            messageTs: "1710000000.101110",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> can you see me?");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("Bot cannot see you", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anyone_workspace_member_where_bot_is_not_in_channel_is_rejected()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Anyone);
        SlackApi.Clear();
        SlackApi.Responder = request => ScriptedMember(request, RegularMemberJson("U_OTHER"), conversation: """
            {"ok":false,"error":"not_in_channel"}
            """);

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-live-anyone-not-in-channel",
            messageTs: "1710000000.101120",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> private channel?");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("Bot cannot see you", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anyone_connect_stranger_is_rejected_before_any_conversation_call()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Anyone);
        SlackApi.Clear();
        SlackApi.Responder = request => ScriptedMember(request, MemberJson("U_OTHER", teamId: "T_CONNECT", stranger: true));

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-live-anyone-stranger",
            messageTs: "1710000000.101130",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> external collaborator");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("regular member", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);
        var recorded = Assert.Single(SlackApi.Requests);
        Assert.EndsWith(SlackMemberIdentityPortAdapter.UsersInfoEndpoint, recorded.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anyone_api_failure_fails_closed_without_creating_resources()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Anyone);
        SlackApi.Clear();
        SlackApi.Responder = _ => throw new HttpRequestException("connection refused");

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-live-anyone-failure",
            messageTs: "1710000000.101140",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> are you there?");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("could not be confirmed", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-live-anyone-failure")
            .ToListAsync());
    }

    [Fact]
    public async Task Owner_is_accepted_under_anyone_without_any_slack_call()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Anyone);
        SlackApi.Clear();

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-live-owner",
            messageTs: "1710000000.101200",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OWNER",
            text: "<@U123> owner task");

        Assert.Equal("accepted", data.GetProperty("kind").GetString());
        Assert.Empty(SlackApi.Requests);
    }

    [Fact]
    public async Task Dm_from_non_owner_under_anyone_is_rejected_without_any_slack_call()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Anyone);
        SlackApi.Clear();

        var data = await PostDmAsync(
            connection,
            conversationId: "D-live-dm",
            messageTs: "1710000000.101210",
            senderSlackUserId: "U_OTHER",
            text: "DM should stay owner-only");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("owner", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(SlackApi.Requests);
    }

    [Fact]
    public async Task Reject_response_never_leaks_the_bot_token()
    {
        var connection = await CreateConnectionAsync(AccessPolicyKind.Anyone);
        SlackApi.Clear();
        SlackApi.Responder = request => ScriptedMember(request, MemberJson("U_OTHER", teamId: "T_CONNECT"));

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-live-leak",
            messageTs = "1710000000.101220",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_OTHER",
            senderKind = "human",
            text = "<@U123> leak check",
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("xoxb-verified", body, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-legacy", body, StringComparison.Ordinal);
    }

    private static HttpResponseMessage ScriptedMember(
        HttpRequestMessage request,
        string memberJson,
        string? conversation = null)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith(SlackMemberIdentityPortAdapter.UsersInfoEndpoint, StringComparison.Ordinal) == true)
            return SlackApiTestScript.JsonResponse(memberJson);
        if (request.RequestUri?.AbsolutePath.EndsWith(SlackMemberIdentityPortAdapter.ConversationsInfoEndpoint, StringComparison.Ordinal) == true)
            return SlackApiTestScript.JsonResponse(conversation ?? """{"ok":true,"channel":{"id":"C123","is_member":true}}""");
        return SlackApiTestScript.JsonResponse("""{"ok":false,"error":"unexpected_slack_api_call"}""");
    }

    private static string RegularMemberJson(string userId) => MemberJson(userId, teamId: "T123");

    private static string MemberJson(string userId, string teamId, bool deleted = false, bool stranger = false) =>
        JsonSerializer.Serialize(new { ok = true, user = new { id = userId, team_id = teamId, deleted = deleted, is_bot = false, is_app_user = false, is_restricted = false, is_ultra_restricted = false, is_stranger = stranger } });
}
