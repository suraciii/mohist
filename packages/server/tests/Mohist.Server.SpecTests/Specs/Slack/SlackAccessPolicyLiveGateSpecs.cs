using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Slack.Ports;
using Mohist.Server.TestSupport;
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

    private static string MemberJson(string userId, string teamId) =>
        JsonSerializer.Serialize(new { ok = true, user = new { id = userId, team_id = teamId, deleted = false, is_bot = false, is_app_user = false, is_restricted = false, is_ultra_restricted = false, is_stranger = false } });
}
