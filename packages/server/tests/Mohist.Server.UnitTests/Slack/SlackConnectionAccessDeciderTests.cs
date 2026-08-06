using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackConnectionAccessDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);
    private const string ProjectId = "proj_1";
    private const string ConnectionId = "conn_1";
    private const string TeamId = "T123";
    private const string Owner = "U_OWNER";
    private const string Listed = "U_LISTED";
    private const string Other = "U_OTHER";
    private const string BotToken = "xoxb-live";
    private const string AdapterId = "adapter-A";
    private const string OperatorId = "operator-1";

    [Fact]
    public async Task Owner_only_policy_denies_non_owner_without_any_slack_api_call()
    {
        var context = await NewContextAsync(AccessPolicyKind.OwnerOnly);

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Other, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.False(decision.Allowed);
        Assert.Contains("owner", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.Members.MemberRequests);
        Assert.Empty(context.Members.ConversationRequests);
    }

    [Fact]
    public async Task Dm_is_owner_only_under_any_policy_without_any_slack_api_call()
    {
        var context = await NewContextAsync(AccessPolicyKind.Anyone);

        var nonOwner = await context.Decider.EvaluateAsync(
            context.Connection, Other, TeamId, "D123", isDirectMessage: true, context.Lease);
        var owner = await context.Decider.EvaluateAsync(
            context.Connection, Owner, TeamId, "D123", isDirectMessage: true, context.Lease);

        Assert.False(nonOwner.Allowed);
        Assert.Contains("owner", nonOwner.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(owner.Allowed);
        Assert.Empty(context.Members.MemberRequests);
        Assert.Empty(context.Members.ConversationRequests);
    }

    [Theory]
    [InlineData(AccessPolicyKind.OwnerOnly)]
    [InlineData(AccessPolicyKind.Allowlist)]
    [InlineData(AccessPolicyKind.Anyone)]
    public async Task Owner_channel_invocation_is_allowed_under_every_policy_without_any_slack_api_call(string policy)
    {
        var context = await NewContextAsync(policy);

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Owner, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.True(decision.Allowed);
        Assert.Empty(context.Members.MemberRequests);
        Assert.Empty(context.Members.ConversationRequests);
    }

    [Fact]
    public async Task Allowlist_unlisted_member_is_denied_before_any_slack_api_call()
    {
        var context = await NewContextAsync(AccessPolicyKind.Allowlist, allowlisted: false);

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Other, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.False(decision.Allowed);
        Assert.Contains("allowlist", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.Members.MemberRequests);
        Assert.Empty(context.Members.ConversationRequests);
    }

    [Fact]
    public async Task Allowlist_listed_regular_member_is_allowed_through_users_info()
    {
        var context = await NewContextAsync(AccessPolicyKind.Allowlist, allowlisted: true);
        context.Members.MemberResult = RegularMember();

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Listed, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.True(decision.Allowed);
        var request = Assert.Single(context.Members.MemberRequests);
        Assert.Equal(BotToken, request.BotToken);
        Assert.Equal(Listed, request.SlackUserId);
        Assert.Empty(context.Members.ConversationRequests);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("restricted")]
    [InlineData("ultra_restricted")]
    [InlineData("bot")]
    [InlineData("app_user")]
    [InlineData("stranger")]
    public async Task Allowlist_listed_member_who_is_no_longer_eligible_is_denied(string ineligibleKind)
    {
        var context = await NewContextAsync(AccessPolicyKind.Allowlist, allowlisted: true);
        context.Members.MemberResult = RegularMember() with
        {
            Deleted = ineligibleKind == "deleted",
            IsRestricted = ineligibleKind == "restricted",
            IsUltraRestricted = ineligibleKind == "ultra_restricted",
            IsBot = ineligibleKind == "bot",
            IsAppUser = ineligibleKind == "app_user",
            IsStranger = ineligibleKind == "stranger",
        };

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Listed, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.False(decision.Allowed);
        Assert.Contains("regular member", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Allowlist_listed_member_from_another_workspace_is_denied()
    {
        var context = await NewContextAsync(AccessPolicyKind.Allowlist, allowlisted: true);
        context.Members.MemberResult = RegularMember() with { TeamId = "T_OTHER" };

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Listed, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.False(decision.Allowed);
        Assert.Contains("regular member", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Allowlist_listed_member_with_unconfirmed_identity_is_denied()
    {
        var context = await NewContextAsync(AccessPolicyKind.Allowlist, allowlisted: true);
        context.Members.MemberResult = new(false, ErrorClass: "transport_error");

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Listed, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.False(decision.Allowed);
        Assert.Contains("could not be confirmed", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Allowlist_listed_unknown_user_is_denied_as_not_a_member()
    {
        var context = await NewContextAsync(AccessPolicyKind.Allowlist, allowlisted: true);
        context.Members.MemberResult = new(false, ErrorClass: "user_not_found");

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Listed, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.False(decision.Allowed);
        Assert.Contains("regular member", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Allowlist_without_lease_context_is_denied_without_any_slack_api_call()
    {
        var context = await NewContextAsync(AccessPolicyKind.Allowlist, allowlisted: true);

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Listed, TeamId, "C123", isDirectMessage: false, leaseContext: null);

        Assert.False(decision.Allowed);
        Assert.Contains("could not be confirmed", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.Members.MemberRequests);
    }

    [Fact]
    public async Task Allowlist_with_a_stale_lease_is_denied_without_any_slack_api_call()
    {
        var context = await NewContextAsync(AccessPolicyKind.Allowlist, allowlisted: true);

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Listed, TeamId, "C123", isDirectMessage: false,
            context.LeaseFor("lease_stale"));

        Assert.False(decision.Allowed);
        Assert.Contains("could not be confirmed", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.Members.MemberRequests);
    }

    [Fact]
    public async Task Anyone_regular_member_in_a_bot_channel_is_allowed()
    {
        var context = await NewContextAsync(AccessPolicyKind.Anyone);
        context.Members.MemberResult = RegularMember(Other);
        context.Members.ConversationResult = new(true, IsMember: true);

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Other, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.True(decision.Allowed);
        var memberRequest = Assert.Single(context.Members.MemberRequests);
        Assert.Equal(BotToken, memberRequest.BotToken);
        var conversationRequest = Assert.Single(context.Members.ConversationRequests);
        Assert.Equal(BotToken, conversationRequest.BotToken);
        Assert.Equal("C123", conversationRequest.ConversationId);
    }

    [Fact]
    public async Task Anyone_regular_member_where_bot_is_not_a_channel_member_is_denied()
    {
        var context = await NewContextAsync(AccessPolicyKind.Anyone);
        context.Members.MemberResult = RegularMember(Other);
        context.Members.ConversationResult = new(true, IsMember: false);

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Other, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.False(decision.Allowed);
        Assert.Contains("Bot cannot see you", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anyone_regular_member_where_bot_is_not_in_channel_is_denied()
    {
        var context = await NewContextAsync(AccessPolicyKind.Anyone);
        context.Members.MemberResult = RegularMember(Other);
        context.Members.ConversationResult = new(false, ErrorClass: "not_in_channel");

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Other, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.False(decision.Allowed);
        Assert.Contains("Bot cannot see you", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anyone_with_a_conversation_api_failure_is_denied()
    {
        var context = await NewContextAsync(AccessPolicyKind.Anyone);
        context.Members.MemberResult = RegularMember(Other);
        context.Members.ConversationResult = new(false, ErrorClass: "transport_error");

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Other, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.False(decision.Allowed);
        Assert.Contains("could not be confirmed", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anyone_stranger_is_denied_before_any_conversation_call()
    {
        var context = await NewContextAsync(AccessPolicyKind.Anyone);
        context.Members.MemberResult = RegularMember() with { TeamId = "T_OTHER", IsStranger = true };

        var decision = await context.Decider.EvaluateAsync(
            context.Connection, Other, TeamId, "C123", isDirectMessage: false, context.Lease);

        Assert.False(decision.Allowed);
        Assert.Contains("regular member", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.Members.ConversationRequests);
    }

    [Fact]
    public async Task No_deny_reason_leaks_the_bot_token()
    {
        var context = await NewContextAsync(AccessPolicyKind.Anyone);
        context.Members.MemberResult = new(false, ErrorClass: "transport_error");
        context.Members.ConversationResult = new(false, ErrorClass: "transport_error");
        var decisions = new[]
        {
            await context.Decider.EvaluateAsync(
                context.Connection, Other, TeamId, "C123", isDirectMessage: false, context.Lease),
            await context.Decider.EvaluateAsync(
                context.Connection, Other, TeamId, "C123", isDirectMessage: false, leaseContext: null),
            await context.Decider.EvaluateAsync(
                context.Connection, Other, TeamId, "C123", isDirectMessage: true, context.Lease),
        };

        foreach (var decision in decisions)
            Assert.DoesNotContain(BotToken, decision.Reason, StringComparison.Ordinal);
    }

    private static async Task<DeciderContext> NewContextAsync(string policy, bool allowlisted = false)
    {
        var clock = new FakeTimeProvider(Now);
        var targets = new InMemorySlackLeaseTargetProvider();
        var secrets = new FakeSecretResolver();
        var targetRef = new SlackLeaseTargetRef.Connection(ProjectId, ConnectionId);
        targets.Add(Target(targetRef, "A123", secrets));
        secrets.Put(targetRef, SecretKind.AppToken, "xapp-live");
        secrets.Put(targetRef, SecretKind.BotToken, BotToken);
        var leases = new SlackAdapterLeaseService(
            new InMemorySlackLeaseStore(), targets, secrets, clock);
        var leaseId = await leases.AcquireRuntimeLeaseAsync(OperatorId, targetRef, AdapterId);
        Assert.NotNull(leaseId);

        var allowedMembers = new FakeAllowedMemberStore(allowlisted ? Listed : null);
        var members = new FakeSlackMemberIdentityPort();
        var decider = new SlackConnectionAccessDecider(allowedMembers, members);
        return new DeciderContext(
            decider, members, leases, leaseId.LeaseId,
            new AgentConnection
            {
                Id = ConnectionId,
                ProjectId = ProjectId,
                WorkspaceTeamId = TeamId,
                OwnerSlackUserId = Owner,
                AccessPolicy = policy,
            });
    }

    private static SlackMemberIdentityResult RegularMember(string userId = Listed) => new(
        Confirmed: true,
        UserId: userId,
        TeamId: TeamId);

    private static SlackLeaseTarget Target(SlackLeaseTargetRef @ref, string appId, FakeSecretResolver _) =>
        new(@ref, appId, Active: true, AppLevelTokenProvisioned: true, BotTokenProvisioned: true,
            CredentialVerified: true,
            SecretStoreAddress.ForAgentConnection(ProjectId, ConnectionId, SecretKind.AppToken),
            SecretStoreAddress.ForAgentConnection(ProjectId, ConnectionId, SecretKind.BotToken),
            CandidateAppLevelTokenAddress: null);

    private sealed record DeciderContext(
        SlackConnectionAccessDecider Decider,
        FakeSlackMemberIdentityPort Members,
        SlackAdapterLeaseService Leases,
        string LeaseId,
        AgentConnection Connection)
    {
        public SlackLeaseContext Lease => LeaseFor(LeaseId);

        public SlackLeaseContext LeaseFor(string leaseId) => new(
            OperatorId, leaseId, AdapterId,
            (targetRef, ct) => Leases.ResolveRuntimeLeaseBotTokenAsync(
                OperatorId, targetRef, leaseId, AdapterId, ct));
    }

    private sealed class FakeAllowedMemberStore(string? allowedUserId) : ISlackConnectionAllowedMemberStore
    {
        public Task<bool> IsAllowedAsync(
            string projectId, string connectionId, string slackUserId, CancellationToken ct = default) =>
            Task.FromResult(allowedUserId is not null
                && string.Equals(allowedUserId, slackUserId, StringComparison.Ordinal));
    }

    private sealed class FakeSecretResolver : ISlackLeaseSecretResolver
    {
        private readonly Dictionary<SecretStoreAddress, string> _values = new();

        public void Put(SlackLeaseTargetRef @ref, SecretKind kind, string token) =>
            _values[AddressFor(@ref, kind)] = token;

        public Task<string?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(address, out var token) ? token : null);

        private static SecretStoreAddress AddressFor(SlackLeaseTargetRef @ref, SecretKind kind) =>
            @ref switch
            {
                SlackLeaseTargetRef.Manager manager =>
                    SecretStoreAddress.ForSlackWorkspaceEnrollment(manager.EnrollmentId, kind),
                SlackLeaseTargetRef.Connection connection =>
                    SecretStoreAddress.ForAgentConnection(connection.ProjectId, connection.ConnectionId, kind),
                _ => throw new InvalidOperationException("Unsupported lease target ref."),
            };
    }
}
