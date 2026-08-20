using Mohist.Server.Runner.Grains;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class ManagerExecutionCredentialsTests
{
    private static readonly ManagerExecutionOrigin Origin = new(
        "T_WORKSPACE",
        "D_MANAGER",
        "1710000000.000001",
        "1710000000.000002",
        "U_ACTOR",
        "enrollment-1",
        "session-1",
        "slack:session-1:input-1");

    [Fact]
    public void Issues_distinct_credentials_and_keeps_only_hash_metadata()
    {
        var store = new ManagerExecutionLeaseStore();
        var epoch = new ManagerDeploymentEpoch(store);
        var issuer = new ManagerExecutionCapabilityIssuer(store, epoch);
        var now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");

        var first = issuer.Issue(new("execution-1", Origin, now, TimeSpan.FromMinutes(5)));
        var second = issuer.Issue(new("execution-2", Origin with { DispatchRef = "slack:session-1:input-2" }, now, TimeSpan.FromMinutes(5)));

        Assert.NotEqual(first.ManagementCredential, first.ReplyCredential);
        Assert.NotEqual(first.ManagementCredential, second.ManagementCredential);
        Assert.NotEqual(first.ReplyCredential, second.ReplyCredential);
        Assert.Equal(4, store.Count);
        Assert.Null(store.Find(first.ManagementCredential));
        Assert.Null(store.Find(first.ReplyCredential));

        var allowed = issuer.Validate(
            first.ManagementCredential,
            ManagerExecutionLeaseKind.Management,
            "workspace.status",
            first.ExecutionId,
            Origin,
            now.AddSeconds(1));
        Assert.True(allowed.Allowed);
        Assert.Equal(ManagerExecutionLeaseKind.Management, allowed.Lease!.Kind);
    }

    [Fact]
    public void Rejects_expiry_scope_origin_and_current_epoch_changes()
    {
        var store = new ManagerExecutionLeaseStore();
        var epoch = new ManagerDeploymentEpoch(store);
        var issuer = new ManagerExecutionCapabilityIssuer(store, epoch);
        var now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        var grant = issuer.Issue(new("execution-1", Origin, now, TimeSpan.FromMinutes(1)));

        Assert.Equal(
            "manager_credential_expired",
            issuer.Validate(grant.ManagementCredential, ManagerExecutionLeaseKind.Management,
                "workspace.status", grant.ExecutionId, Origin, now.AddMinutes(1)).Code);
        Assert.Equal(
            "manager_credential_scope_mismatch",
            issuer.Validate(grant.ManagementCredential, ManagerExecutionLeaseKind.Reply,
                "manager.reply", grant.ExecutionId, Origin, now).Code);
        Assert.Equal(
            "manager_origin_mismatch",
            issuer.Validate(grant.ManagementCredential, ManagerExecutionLeaseKind.Management,
                "workspace.status", grant.ExecutionId, Origin with { ActorId = "U_REMOVED" }, now).Code);

        epoch.Advance();
        Assert.Equal(
            "manager_epoch_changed",
            issuer.Validate(grant.ManagementCredential, ManagerExecutionLeaseKind.Management,
                "workspace.status", grant.ExecutionId, Origin, now).Code);
    }

    [Fact]
    public void Graceful_invalidation_revokes_both_leases_and_store_failure_denies()
    {
        var store = new ManagerExecutionLeaseStore();
        var epoch = new ManagerDeploymentEpoch(store);
        var issuer = new ManagerExecutionCapabilityIssuer(store, epoch);
        var now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        var grant = issuer.Issue(new("execution-1", Origin, now, TimeSpan.FromMinutes(5)));

        epoch.Invalidate();

        Assert.Equal(2, store.RevokeExecution(grant.ExecutionId));
        Assert.Equal("manager_authorization_unavailable", issuer.Validate(
            grant.ReplyCredential,
            ManagerExecutionLeaseKind.Reply,
            "manager.reply",
            grant.ExecutionId,
            Origin,
            now).Code);
    }

    [Fact]
    public void Runtime_capability_gate_rejects_mixed_version_runners()
    {
        var old = new RunnerInfo("runner-1", ["execution-source-v1"], "host", null);
        var current = old with { Capabilities = ManagerExecutionRuntimeCapabilities.Required.ToArray() };

        Assert.False(ManagerExecutionRuntimeCapabilities.Supports(old));
        Assert.True(ManagerExecutionRuntimeCapabilities.Supports(current));
    }
}
