using Mohist.Server.Runner.Grains;
using Mohist.Server.Slack.Services;
using System.Security.Cryptography;
using Xunit;

namespace Mohist.Server.L0Tests.Slack;

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
    public async Task Separate_server_epochs_read_the_shared_current_epoch_on_every_operation()
    {
        var store = new SharedEpochStore();
        var firstServerEpoch = new ManagerDeploymentEpoch(store);
        await firstServerEpoch.StartAsync(default);
        var firstIssuer = new ManagerExecutionCapabilityIssuer(store, firstServerEpoch);
        var now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        var oldGrant = firstIssuer.Issue(new("execution-old", Origin, now, TimeSpan.FromMinutes(5)));

        var secondServerEpoch = new ManagerDeploymentEpoch(store);
        await secondServerEpoch.StartAsync(default);

        Assert.Equal(secondServerEpoch.Current, firstServerEpoch.Current);
        Assert.Equal(
            "manager_epoch_changed",
            firstIssuer.Validate(oldGrant.ManagementCredential, ManagerExecutionLeaseKind.Management,
                "workspace.status", oldGrant.ExecutionId, Origin, now).Code);

        var freshOrigin = Origin with { DispatchRef = "slack:session-1:fresh" };
        var freshGrant = firstIssuer.Issue(new("execution-fresh", freshOrigin, now, TimeSpan.FromMinutes(5)));
        Assert.Equal(secondServerEpoch.Current, freshGrant.DeploymentEpoch);
        Assert.True(firstIssuer.Validate(freshGrant.ManagementCredential, ManagerExecutionLeaseKind.Management,
            "workspace.status", freshGrant.ExecutionId, freshOrigin, now).Allowed);
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
    public async Task Host_stopping_revokes_once_before_stop()
    {
        var store = new SharedEpochStore();
        var epoch = new ManagerDeploymentEpoch(store);

        await epoch.StoppingAsync(default);
        Assert.False(epoch.Available);
        Assert.Equal(1, store.RevokeAllCount);

        await epoch.StopAsync(default);
        Assert.Equal(1, store.RevokeAllCount);
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

file sealed class SharedEpochStore : IManagerExecutionLeaseStore
{
    private readonly Dictionary<string, ManagerExecutionLeaseMetadata> _leases = new(StringComparer.Ordinal);
    private string? _epoch;

    public bool Available => true;
    public bool IsShared => true;
    public int Count => _leases.Count;
    public int RevokeAllCount { get; private set; }

    public string? ReadDeploymentEpoch() => _epoch ??= "mepoch_initial";

    public string? AdvanceDeploymentEpoch()
    {
        _epoch = $"mepoch_{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
        RevokeAll();
        return _epoch;
    }

    public void Put(ManagerExecutionLeaseMetadata lease) => _leases[lease.CredentialHash] = lease;

    public ManagerExecutionLeaseMetadata? Find(string credentialHash) =>
        FindIncludingRevoked(credentialHash) is { Active: true } lease ? lease : null;

    public ManagerExecutionLeaseMetadata? FindIncludingRevoked(string credentialHash) =>
        _leases.TryGetValue(credentialHash, out var lease) ? lease : null;

    public int RevokeExecution(string executionId) => Revoke(lease => lease.ExecutionId == executionId);

    public int RevokeExecutionPrefix(string executionPrefix) =>
        Revoke(lease => lease.ExecutionId.StartsWith(executionPrefix, StringComparison.Ordinal));

    public int RevokeAll()
    {
        RevokeAllCount++;
        return Revoke(_ => true);
    }

    public int RemoveExpired(DateTimeOffset now) => Remove(lease => lease.ExpiresAt <= now);

    private int Revoke(Func<ManagerExecutionLeaseMetadata, bool> predicate)
    {
        var count = 0;
        foreach (var (hash, lease) in _leases.ToArray())
        {
            if (lease.Active && predicate(lease))
            {
                _leases[hash] = lease with { Active = false };
                count++;
            }
        }
        return count;
    }

    private int Remove(Func<ManagerExecutionLeaseMetadata, bool> predicate)
    {
        var count = 0;
        foreach (var (hash, lease) in _leases.ToArray())
        {
            if (predicate(lease) && _leases.Remove(hash)) count++;
        }
        return count;
    }
}
