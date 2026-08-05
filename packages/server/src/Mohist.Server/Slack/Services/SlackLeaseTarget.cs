using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// A lease target as the lease core sees it: identity, readiness, and the
/// secret <em>addresses</em> (never plaintext) the service resolves at
/// issuance time. Addresses are references, not secrets, and never appear
/// in discovery / state / list DTOs.
/// </summary>
public sealed record SlackLeaseTarget(
    SlackLeaseTargetRef Ref,
    string ExpectedAppId,
    bool Active,
    bool AppLevelTokenProvisioned,
    bool BotTokenProvisioned,
    bool CredentialVerified,
    SecretStoreAddress AppLevelTokenAddress,
    SecretStoreAddress BotTokenAddress);

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
}

/// <summary>
/// Deterministic in-memory target registry. Not registered conventionally:
/// a future slice binds the real enrollment/connection-backed provider.
/// </summary>
public sealed class InMemorySlackLeaseTargetProvider : ISlackLeaseTargetProvider
{
    private readonly Dictionary<string, SlackLeaseTarget> _targets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);

    public InMemorySlackLeaseTargetProvider Add(SlackLeaseTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _targets[target.Ref.TargetKey] = target;
        if (target.CredentialVerified)
            _verified.Add(target.Ref.TargetKey);
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

    private SlackLeaseTarget WithVerified(SlackLeaseTarget target) =>
        target with { CredentialVerified = _verified.Contains(target.Ref.TargetKey) };

    private static void RequireOperator(string operatorId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
}
