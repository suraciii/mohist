using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Slack;

/// <summary>
/// Covers the issue-526 access-policy substrate. The Owner-only
/// access decision is the substrate that the wider policy work in
/// T-002/T-003 layers on top of; this file pins the behavior under
/// the default <c>owner_only</c> policy so a future widening
/// (allowlist, anyone) cannot silently regress the Owner path.
/// </summary>
[Collection("SlackApiSurface")]
[Trait("level", "L1")]
public sealed partial class SlackAccessPolicyApiSpecs : IAsyncLifetime
{
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);
    private readonly MohistIntegrationFixture _fixture;

    public SlackAccessPolicyApiSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

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
    public async Task Non_owner_root_mention_under_default_owner_only_is_rejected_with_no_resources()
    {
        var connection = await CreateConnectionAsync();
        var data = await PostChannelAsync(
            connection,
            conversationId: "C-access-non-owner-reject",
            messageTs: "1710000000.100110",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> I should not be able to invoke");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("owner", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-access-non-owner-reject")
            .ToListAsync());
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id)
            .ToListAsync());
    }

    private async Task<JsonElement> PostChannelAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string senderSlackUserId,
        string text)
    {
        var body = new
        {
            apiAppId = "A123",
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            mentionedUserIds = mentions,
            senderSlackUserId,
            senderKind = "human",
            text,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";

    private async Task<AgentConnection> CreateConnectionAsync(
        string? accessPolicy = null,
        IReadOnlyList<string>? allowMembers = null)
    {
        // Distinct secret values prove the live identity gate resolves the
        // verified Agent App Bot token through the runtime lease seam, never
        // the dead connection-scoped legacy addresses.
        var seeded = await SlackManagedConnectionSeed.CreateAsync(_fixture, new SlackSeedOptions
        {
            AppToken = "xapp",
            BotToken = "xoxb-verified",
            ConnectionAppToken = "xapp-legacy",
            ConnectionBotToken = "xoxb-legacy",
            AccessPolicy = accessPolicy ?? AccessPolicyKind.OwnerOnly,
            AllowedMembers = allowMembers,
        });
        _connectionLeases[seeded.Connection.Id] = seeded.LeaseId;
        return seeded.Connection;
    }
}
