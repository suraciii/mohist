using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Direct unit-style coverage for the <see cref="SlackConnectionAccessDecider"/>
/// decision rules. Mirrors the spec scenarios in
/// <c>openspec/changes/issue-526/specs/channel-access-policy/spec.md</c>
/// at the level of the decider in isolation — no ingress plumbing,
/// no Slack API. The owner_only + DM rules are pinned here so
/// widening the policy in a follow-up task cannot regress the
/// substrate.
/// </summary>
public sealed class SlackConnectionAccessDeciderSpecs : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly TestSqliteDatabase _database;
    private readonly SlackConnectionAccessDecider _decider;

    public SlackConnectionAccessDeciderSpecs()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(_database.Options);
        var allowedMembers = new Mohist.Server.Infrastructure.Slack.SlackConnectionAllowedMemberStore(factory, _time);
        _decider = new SlackConnectionAccessDecider(factory, allowedMembers);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Owner_is_authorized_under_owner_only()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.OwnerOnly);

        var decision = await _decider.EvaluateAsync(
            connection, "U_OWNER", "T123", "C-channel", isDirectMessage: false);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Non_owner_is_rejected_under_owner_only()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.OwnerOnly);

        var decision = await _decider.EvaluateAsync(
            connection, "U_OTHER", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
        Assert.Contains("owner", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Owner_is_authorized_under_any_channel_policy()
    {
        foreach (var policy in new[] { AccessPolicyKind.OwnerOnly, AccessPolicyKind.Allowlist, AccessPolicyKind.Anyone })
        {
            var connection = NewConnection("U_OWNER", policy);
            var decision = await _decider.EvaluateAsync(
                connection, "U_OWNER", "T123", "C-channel", isDirectMessage: false);
            Assert.True(decision.Allowed, $"Owner should always be authorized under {policy}");
        }
    }

    [Fact]
    public async Task Dm_is_owner_only_regardless_of_channel_policy()
    {
        foreach (var policy in new[] { AccessPolicyKind.OwnerOnly, AccessPolicyKind.Allowlist, AccessPolicyKind.Anyone })
        {
            var connection = NewConnection("U_OWNER", policy);
            var decision = await _decider.EvaluateAsync(
                connection, "U_OTHER", "T123", "D-DM", isDirectMessage: true);
            Assert.False(decision.Allowed, $"Non-Owner DM should be rejected under channel policy {policy}");
            Assert.Contains("owner", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Dm_owner_is_authorized_under_any_channel_policy()
    {
        foreach (var policy in new[] { AccessPolicyKind.OwnerOnly, AccessPolicyKind.Allowlist, AccessPolicyKind.Anyone })
        {
            var connection = NewConnection("U_OWNER", policy);
            var decision = await _decider.EvaluateAsync(
                connection, "U_OWNER", "T123", "D-DM", isDirectMessage: true);
            Assert.True(decision.Allowed, $"Owner DM should be authorized under channel policy {policy}");
        }
    }

    [Fact]
    public async Task Non_owner_under_allowlist_is_rejected_when_not_listed()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);

        var decision = await _decider.EvaluateAsync(
            connection, "U_NOTLISTED", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
        Assert.Contains("owner", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_owner_under_allowlist_is_authorized_when_listed()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");

        var decision = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Decider_reads_current_policy_from_column()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.OwnerOnly);
        connection.AccessPolicy = AccessPolicyKind.Allowlist;

        var listedDecision = await _decider.EvaluateAsync(
            connection, "U_OWNER", "T123", "C-channel", isDirectMessage: false);
        Assert.True(listedDecision.Allowed);

        var unlistedDecision = await _decider.EvaluateAsync(
            connection, "U_NOTLISTED", "T123", "C-channel", isDirectMessage: false);
        Assert.False(unlistedDecision.Allowed);
    }

    private AgentConnection NewConnection(string ownerSlackUserId, string accessPolicy) => new()
    {
        Id = $"conn_{Guid.NewGuid():N}",
        ProjectId = "project-1",
        AgentId = "agent-1",
        ProviderKind = ConnectionProviderKind.Slack,
        WorkspaceTeamId = "T123",
        AppId = "A123",
        BotUserId = "U123",
        BotName = "Mohist",
        OwnerSlackUserId = ownerSlackUserId,
        AccessPolicy = accessPolicy,
    };

    private async Task SeedAllowedMemberAsync(AgentConnection connection, string slackUserId)
    {
        await using var db = _database.CreateContext();
        db.SlackConnectionAllowedMembers.Add(new Mohist.Server.Infrastructure.Data.Slack.SlackConnectionAllowedMemberRow
        {
            Id = $"slkalm_{Guid.NewGuid():N}",
            ProjectId = connection.ProjectId,
            ConnectionId = connection.Id,
            SlackUserId = slackUserId,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            CreatedAt = _time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }
}
