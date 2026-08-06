using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// A lease target as the lease core sees it: identity, readiness, and the
/// secret <em>addresses</em> (never plaintext) the service resolves at
/// issuance time. Addresses are references, not secrets, and never appear
/// in discovery / state / list DTOs.
/// <para>
/// Runtime addresses (<see cref="AppLevelTokenAddress"/> /
/// <see cref="BotTokenAddress"/>) only ever hold a verified pair once the
/// target is <see cref="CredentialVerified"/>: an unverified candidate is
/// staged under <see cref="CandidateAppLevelTokenAddress"/> and only copied
/// to the runtime addresses after a matching Socket hello. The validation
/// lease therefore always resolves the candidate, never the runtime pair, so
/// a hello can only prove the candidate actually being validated.
/// </para>
/// </summary>
public sealed record SlackLeaseTarget(
    SlackLeaseTargetRef Ref,
    string ExpectedAppId,
    bool Active,
    bool AppLevelTokenProvisioned,
    bool BotTokenProvisioned,
    bool CredentialVerified,
    SecretStoreAddress AppLevelTokenAddress,
    SecretStoreAddress BotTokenAddress,
    SecretStoreAddress? CandidateAppLevelTokenAddress);

/// <summary>
/// Reads lease targets and writes back the Socket hello verification fact.
/// The production implementation maps enrollment readiness / connection
/// state; this slice ships only the in-memory backing store so the lease
/// core is fully exercisable without wiring the enrollment domain.
/// </summary>
public interface ISlackLeaseTargetProvider
{
    Task<IReadOnlyList<SlackLeaseTarget>> GetTargetsAsync(string operatorId, CancellationToken ct = default);

    Task<SlackLeaseTarget?> GetTargetAsync(string operatorId, SlackLeaseTargetRef targetRef, CancellationToken ct = default);

    Task MarkVerifiedAsync(
        string operatorId,
        SlackLeaseTargetRef targetRef,
        string appId,
        DateTimeOffset verifiedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Rejects a Socket hello whose <c>app_id</c> does not match the target's
    /// expected App: delete the unverified candidate (or, during a rotation,
    /// restore the parked previous verified pair) and keep the target
    /// unverified / unbound. Idempotent: a verified, failed or not-provided
    /// target is left untouched. Equivalent to the control-plane rejection
    /// path, so the lease hello cannot bypass it.
    /// </summary>
    Task RejectAsync(
        string operatorId,
        SlackLeaseTargetRef targetRef,
        DateTimeOffset rejectedAt,
        CancellationToken ct = default);
}

/// <summary>
/// Deterministic in-memory target registry. Not registered conventionally:
/// a future slice binds the real enrollment/connection-backed provider.
/// </summary>
public sealed class InMemorySlackLeaseTargetProvider : ISlackLeaseTargetProvider
{
    private readonly Dictionary<string, SlackLeaseTarget> _targets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);

    public InMemorySlackLeaseTargetProvider Add(SlackLeaseTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _targets[target.Ref.TargetKey] = target;
        if (target.CredentialVerified)
            _verified.Add(target.Ref.TargetKey);
        else
            _verified.Remove(target.Ref.TargetKey);
        return this;
    }

    public Task<IReadOnlyList<SlackLeaseTarget>> GetTargetsAsync(string operatorId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        IReadOnlyList<SlackLeaseTarget> snapshot = [.. _targets.Values.Select(WithVerified)];
        return Task.FromResult(snapshot);
    }

    public Task<SlackLeaseTarget?> GetTargetAsync(string operatorId, SlackLeaseTargetRef targetRef, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        return _targets.TryGetValue(targetRef.TargetKey, out var target)
            ? Task.FromResult<SlackLeaseTarget?>(WithVerified(target))
            : Task.FromResult<SlackLeaseTarget?>(null);
    }

    public Task MarkVerifiedAsync(
        string operatorId,
        SlackLeaseTargetRef targetRef,
        string appId,
        DateTimeOffset verifiedAt,
        CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        _verified.Add(targetRef.TargetKey);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The in-memory fake exercises the lease core, not the enrollment domain,
    /// so it only records that a rejection was delegated. Production cleanup
    /// (candidate deletion / previous-pair restore) is owned by
    /// <see cref="EnrollmentSlackLeaseTargetProvider"/> and covered by its
    /// own specs.
    /// </summary>
    public Task RejectAsync(
        string operatorId,
        SlackLeaseTargetRef targetRef,
        DateTimeOffset rejectedAt,
        CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        _rejected.Add(targetRef.TargetKey);
        return Task.CompletedTask;
    }

    public IReadOnlySet<string> RejectedTargets => _rejected;

    private SlackLeaseTarget WithVerified(SlackLeaseTarget target) =>
        target with { CredentialVerified = _verified.Contains(target.Ref.TargetKey) };

    private static void RequireOperator(string operatorId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
}
