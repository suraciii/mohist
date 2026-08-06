using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Non-secret discovery view. Tokens and secret addresses never appear here.
/// </summary>
public sealed record SlackLeaseTargetView(
    string Kind,
    string? EnrollmentId,
    string? WorkspaceTeamId,
    string? ProjectId,
    string? ConnectionId,
    string ExpectedAppId,
    bool Active,
    bool AppLevelTokenProvisioned,
    bool BotTokenProvisioned,
    bool CredentialVerified,
    bool CanAcquireValidation,
    bool CanAcquireRuntime);

/// <summary>
/// Validation lease response. The only lease response that carries the
/// candidate App-level token, and only so the adapter can open one Socket
/// and report a single <c>hello.app_id</c>. No Bot token, no ingress, no
/// outbox grant.
/// </summary>
public sealed record SlackValidationLeaseResult(
    string LeaseId,
    int Generation,
    DateTimeOffset ExpiresAt,
    string ExpectedAppId,
    string AppToken);

/// <summary>
/// Runtime lease response. Issued only after a verified hello and an active
/// target. Carries both Socket and Bot tokens; ingress / outbox are separate
/// operations gated on this lease elsewhere.
/// </summary>
public sealed record SlackRuntimeLeaseResult(
    string LeaseId,
    int Generation,
    DateTimeOffset ExpiresAt,
    string AppToken,
    string BotToken);

public enum SlackHelloOutcome
{
    Verified,
    AppIdMismatch,
    NoLease
}

/// <summary>
/// Renewed lease metadata. Renewal never reissues tokens: the holder already
/// has them and only needs the new expiry plus the fencing tokens.
/// </summary>
public sealed record SlackLeaseRenewalResult(
    string LeaseId,
    string Kind,
    int Generation,
    DateTimeOffset ExpiresAt);

public sealed class SlackAdapterLeaseService(
    ISlackLeaseStore store,
    ISlackLeaseTargetProvider targetProvider,
    ISlackLeaseSecretResolver secretResolver,
    TimeProvider timeProvider,
    AgentConnectionStore? connections = null) : IScopedService
{
    public static readonly TimeSpan ValidationLeaseTtl = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RuntimeLeaseTtl = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<SlackLeaseTargetView>> DiscoverAsync(
        string operatorId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        var targets = await targetProvider.GetTargetsAsync(operatorId, ct);
        return targets
            .Where(target => target.AppLevelTokenProvisioned)
            .Select(ToView)
            .ToList();
    }

    public async Task<SlackValidationLeaseResult?> AcquireValidationLeaseAsync(
        string operatorId, SlackLeaseTargetRef targetRef, string adapterId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        RequireTarget(targetRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        var target = await targetProvider.GetTargetAsync(operatorId, targetRef, ct);
        if (target is null || !target.AppLevelTokenProvisioned || target.CredentialVerified)
            return null;
        // The validation lease proves a candidate: it must resolve the staged
        // candidate App-level token, never the runtime pair.
        if (target.CandidateAppLevelTokenAddress is not { } candidateAddress)
            return null;

        // Resolve before issue: a missing candidate (crash between candidate
        // cleanup and Verified) must fail cleanly, never leaving an inert
        // active lease behind.
        var appToken = await secretResolver.LoadAsync(candidateAddress, ct);
        if (string.IsNullOrWhiteSpace(appToken))
            return null;
        var now = timeProvider.GetUtcNow();
        var lease = await store.IssueAsync(
            target.Ref.TargetKey, SlackLeaseKind.Validation, adapterId, now + ValidationLeaseTtl, now,
            Fingerprint(appToken), ct);
        return new SlackValidationLeaseResult(
            lease.LeaseId, lease.Generation, lease.ExpiresAt, target.ExpectedAppId, appToken);
    }

    public async Task<SlackHelloOutcome> ReportHelloAsync(
        string operatorId, SlackLeaseTargetRef targetRef, string leaseId, string appId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        RequireTarget(targetRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        var target = await targetProvider.GetTargetAsync(operatorId, targetRef, ct);
        if (target is null)
            return SlackHelloOutcome.NoLease;

        var active = await store.GetActiveAsync(target.Ref.TargetKey, ct);
        if (active is null
            || active.Kind != SlackLeaseKind.Validation
            || !string.Equals(active.LeaseId, leaseId, StringComparison.Ordinal))
            return SlackHelloOutcome.NoLease;

        // Credential-generation fence: the lease only proves the candidate it
        // was issued against. A resupplied candidate (new token, same App)
        // makes this lease stale, so its hello fails closed without touching
        // the new candidate — an old token must neither verify nor reject it.
        if (!await CredentialGenerationMatchesAsync(target, active, ct))
            return SlackHelloOutcome.NoLease;

        if (!string.Equals(appId, target.ExpectedAppId, StringComparison.Ordinal))
        {
            // A mismatched hello means the current candidate App-level token
            // does not prove the expected App. Reject exactly like the
            // control-plane route would: delete the candidate (or, during a
            // rotation, restore the parked previous verified pair) and keep
            // the target unverified / unbound. The validation lease itself is
            // not consumed, so a corrected attempt can re-acquire. This is
            // idempotent.
            await targetProvider.RejectAsync(operatorId, target.Ref, timeProvider.GetUtcNow(), ct);
            return SlackHelloOutcome.AppIdMismatch;
        }

        var now = timeProvider.GetUtcNow();
        if (!await store.ConfirmHelloAsync(target.Ref.TargetKey, leaseId, now, ct))
            return SlackHelloOutcome.NoLease;

        await targetProvider.MarkVerifiedAsync(operatorId, target.Ref, appId, now, ct);
        return SlackHelloOutcome.Verified;
    }

    public async Task<SlackRuntimeLeaseResult?> AcquireRuntimeLeaseAsync(
        string operatorId, SlackLeaseTargetRef targetRef, string adapterId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        RequireTarget(targetRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        var target = await targetProvider.GetTargetAsync(operatorId, targetRef, ct);
        if (target is null || !target.CredentialVerified || !target.Active || !target.BotTokenProvisioned)
            return null;

        var appToken = await RequireSecretAsync(target.AppLevelTokenAddress, ct);
        var botToken = await RequireSecretAsync(target.BotTokenAddress, ct);
        // The runtime lease carries the verified pair, so the target must still
        // be Active and Verified after the secrets were resolved; a rotation or
        // disable that raced the resolution must not be handed the pair.
        var fresh = await targetProvider.GetTargetAsync(operatorId, targetRef, ct);
        if (fresh is null || !fresh.Active || !fresh.CredentialVerified)
            return null;
        var now = timeProvider.GetUtcNow();
        var lease = await store.IssueAsync(
            target.Ref.TargetKey, SlackLeaseKind.Runtime, adapterId, now + RuntimeLeaseTtl, now,
            Fingerprint(appToken, botToken), ct);
        return new SlackRuntimeLeaseResult(
            lease.LeaseId, lease.Generation, lease.ExpiresAt, appToken, botToken);
    }

    /// <summary>
    /// Route-level gate: proves the caller still holds the current, unexpired
    /// runtime lease for the target <em>before</em> any inbox/outbox side
    /// effect. Fails closed when the lease was superseded or expired, when
    /// adapter / lease / target do not match, when the target is no longer
    /// active or verified, or when the pinned credential generation no longer
    /// matches (resupplied candidate / rotated verified pair).
    /// </summary>
    public async Task<bool> ValidateRuntimeLeaseAsync(
        string operatorId, SlackLeaseTargetRef targetRef, string leaseId, string adapterId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        RequireTarget(targetRef);
        // A request without lease proof must fail closed like any stale lease:
        // it is not a caller contract violation, so it never throws.
        if (string.IsNullOrWhiteSpace(leaseId) || string.IsNullOrWhiteSpace(adapterId))
            return false;

        var now = timeProvider.GetUtcNow();
        var active = await store.GetActiveAsync(targetRef.TargetKey, ct);
        if (active is null
            || active.Kind != SlackLeaseKind.Runtime
            || !string.Equals(active.LeaseId, leaseId, StringComparison.Ordinal)
            || !string.Equals(active.AdapterId, adapterId, StringComparison.Ordinal)
            || active.ExpiresAt <= now)
            return false;

        // A runtime lease was issued only while the target was Active,
        // CredentialVerified and Bot-token provisioned; it stays valid only
        // while all three still hold.
        var target = await targetProvider.GetTargetAsync(operatorId, targetRef, ct);
        if (target is null || !target.Active || !target.CredentialVerified || !target.BotTokenProvisioned)
            return false;

        // The lease pins the credential generation it was issued against; a
        // resupplied candidate or a rotated verified pair makes it stale.
        return await CredentialGenerationMatchesAsync(target, active, ct);
    }

    /// <summary>
    /// Proves the caller still holds the current runtime lease (the same
    /// fail-closed checks as <see cref="ValidateRuntimeLeaseAsync"/>) and,
    /// only then, resolves the verified Bot token the lease pins from the
    /// target's AgentApp secret address. Used by the access decider so the
    /// live identity gate runs under the same lease fence as every
    /// adapter-facing route and never falls back to a legacy
    /// connection-scoped secret address. Returns null on any failure; the
    /// token never leaves the call chain.
    /// </summary>
    public async Task<string?> ResolveRuntimeLeaseBotTokenAsync(
        string operatorId, SlackLeaseTargetRef targetRef, string leaseId, string adapterId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        RequireTarget(targetRef);
        if (string.IsNullOrWhiteSpace(leaseId) || string.IsNullOrWhiteSpace(adapterId))
            return null;

        if (!await ValidateRuntimeLeaseAsync(operatorId, targetRef, leaseId, adapterId, ct))
            return null;
        var target = await targetProvider.GetTargetAsync(operatorId, targetRef, ct);
        return target is null ? null : await secretResolver.LoadAsync(target.BotTokenAddress, ct);
    }

    /// <summary>
    /// Route-level gate for a Manager target addressed by enrollment id (the
    /// manager adapter delivery routes). Resolves the stored workspace team
    /// inside the target provider so the caller never touches storage, then
    /// delegates to <see cref="ValidateRuntimeLeaseAsync"/>. Fail-closed
    /// (false) when the enrollment is gone or the lease is stale / expired /
    /// mismatched.
    /// </summary>
    public async Task<bool> ValidateManagerRuntimeLeaseByEnrollmentAsync(
        string operatorId, string enrollmentId, string leaseId, string adapterId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentId);
        var manager = await targetProvider.ResolveManagerByEnrollmentAsync(enrollmentId, ct);
        return manager is null
            ? false
            : await ValidateRuntimeLeaseAsync(operatorId, manager, leaseId, adapterId, ct);
    }

    /// <summary>
    /// Route-level gate for a Manager target addressed by workspace team (the
    /// manager ingress route). Resolves the active enrollment inside the
    /// target provider so the caller never touches storage, then delegates to
    /// <see cref="ValidateRuntimeLeaseAsync"/>. Fail-closed (false) when no
    /// active enrollment exists or the lease is stale / expired / mismatched.
    /// </summary>
    public async Task<bool> ValidateManagerRuntimeLeaseByTeamAsync(
        string operatorId, string workspaceTeamId, string leaseId, string adapterId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        var manager = await targetProvider.ResolveManagerByTeamAsync(workspaceTeamId, ct);
        return manager is null
            ? false
            : await ValidateRuntimeLeaseAsync(operatorId, manager, leaseId, adapterId, ct);
    }

    public async Task<SlackLeaseRenewalResult?> RenewLeaseAsync(
        string operatorId, SlackLeaseTargetRef targetRef, string leaseId, string adapterId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        RequireTarget(targetRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        var now = timeProvider.GetUtcNow();
        var active = await store.GetActiveAsync(targetRef.TargetKey, ct);
        if (active is null
            || !string.Equals(active.LeaseId, leaseId, StringComparison.Ordinal)
            || !string.Equals(active.AdapterId, adapterId, StringComparison.Ordinal)
            || active.ExpiresAt <= now)
            return null;

        // The lease kind and the target state must still agree: a validation
        // lease only renews while the target is an unverified candidate, a
        // runtime lease only while the target is Active and Verified.
        var target = await targetProvider.GetTargetAsync(operatorId, targetRef, ct);
        if (target is null || !target.Active)
            return null;
        if (active.Kind == SlackLeaseKind.Runtime && !target.CredentialVerified)
            return null;
        if (active.Kind == SlackLeaseKind.Validation && target.CredentialVerified)
            return null;
        // The credential generation the lease was issued against must still be
        // current; a resupplied candidate or a rotated verified pair makes the
        // lease stale, and its holder must re-acquire.
        if (!await CredentialGenerationMatchesAsync(target, active, ct))
            return null;

        var ttl = active.Kind == SlackLeaseKind.Runtime ? RuntimeLeaseTtl : ValidationLeaseTtl;
        var renewed = await store.RenewAsync(targetRef.TargetKey, leaseId, adapterId, now + ttl, now, ct);
        if (renewed is null)
            return null;
        // A renewed runtime lease is the adapter liveness signal for the
        // connection diagnostic (SlackSetupVerifier.IsAdapterOnline): the
        // adapter renews while its Socket is connected, so the heartbeat
        // follows the lease and flips stale when the adapter stops renewing.
        if (connections is not null && active.Kind == SlackLeaseKind.Runtime && targetRef is SlackLeaseTargetRef.Connection connectionRef)
        {
            await connections.UpdateAsync(
                connectionRef.ProjectId,
                connectionRef.ConnectionId,
                new HashSet<string>(StringComparer.Ordinal) { "lastHeartbeatAt" },
                lastHeartbeatAt: now,
                ct: ct);
        }
        return new SlackLeaseRenewalResult(renewed.LeaseId, renewed.Kind, renewed.Generation, renewed.ExpiresAt);
    }

    private async Task<bool> CredentialGenerationMatchesAsync(
        SlackLeaseTarget target, SlackLeaseRecord active, CancellationToken ct)
    {
        if (active.Kind == SlackLeaseKind.Validation)
        {
            if (target.CandidateAppLevelTokenAddress is not { } candidateAddress)
                return false;
            var candidate = await secretResolver.LoadAsync(candidateAddress, ct);
            return !string.IsNullOrWhiteSpace(candidate)
                && string.Equals(active.CredentialFingerprint, Fingerprint(candidate), StringComparison.Ordinal);
        }

        var app = await secretResolver.LoadAsync(target.AppLevelTokenAddress, ct);
        var bot = await secretResolver.LoadAsync(target.BotTokenAddress, ct);
        return !string.IsNullOrWhiteSpace(app)
            && !string.IsNullOrWhiteSpace(bot)
            && string.Equals(active.CredentialFingerprint, Fingerprint(app, bot), StringComparison.Ordinal);
    }

    private async Task<string> RequireSecretAsync(SecretStoreAddress address, CancellationToken ct)
    {
        var token = await secretResolver.LoadAsync(address, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "The lease target is marked provisioned but its secret could not be resolved.");
        return token;
    }

    private static string Fingerprint(params string[] tokens) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Concat(tokens))));

    private static SlackLeaseTargetView ToView(SlackLeaseTarget target) => new(
        Kind: target.Ref.Kind,
        EnrollmentId: (target.Ref as SlackLeaseTargetRef.Manager)?.EnrollmentId,
        WorkspaceTeamId: (target.Ref as SlackLeaseTargetRef.Manager)?.WorkspaceTeamId,
        ProjectId: (target.Ref as SlackLeaseTargetRef.Connection)?.ProjectId,
        ConnectionId: (target.Ref as SlackLeaseTargetRef.Connection)?.ConnectionId,
        ExpectedAppId: target.ExpectedAppId,
        Active: target.Active,
        AppLevelTokenProvisioned: target.AppLevelTokenProvisioned,
        BotTokenProvisioned: target.BotTokenProvisioned,
        CredentialVerified: target.CredentialVerified,
        CanAcquireValidation: target.AppLevelTokenProvisioned && !target.CredentialVerified,
        CanAcquireRuntime: target.CredentialVerified && target.Active && target.BotTokenProvisioned);

    private static void RequireOperator(string operatorId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);

    private static void RequireTarget(SlackLeaseTargetRef targetRef) =>
        ArgumentNullException.ThrowIfNull(targetRef);
}
