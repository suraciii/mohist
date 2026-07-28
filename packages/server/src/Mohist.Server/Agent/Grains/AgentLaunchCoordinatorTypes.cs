using System.Security.Cryptography;
using System.Text;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Participant command kinds the coordinator advances through. The
/// coordinator persists at most one pending command at a time so a
/// recovery reminder resumes the same step the partial run left
/// behind.
/// </summary>
public enum AgentLaunchCoordinatorCommand
{
    None,
    PrepareJob,
    EnsureInitialLaunch,
    SubmitJob,
}

/// <summary>
/// One-at-a-time command fence persisted on the coordinator grain.
/// Each participant call clears the entry on
/// <c>AlreadyApplied</c>/successful acknowledgement so the next
/// invocation can persist the next step. The fence survives
/// activation loss and process restart; the
/// <see cref="AgentLaunchCoordinatorGrain"/> reminder ticks until
/// every command has been acknowledged.
/// </summary>
public sealed record AgentLaunchCoordinatorPending(
    string CommandId,
    AgentLaunchCoordinatorCommand Kind,
    string? Payload,
    string? ExpectedRevision);

/// <summary>
/// Durable plan record for a manual launch. The coordinator is a
/// narrow application process manager: it stores only the canonical
/// request, the resolved Agent snapshot, the generated
/// Job/Session/Input/Turn ids, the request fingerprint, and the
/// pending command fence. It does not mirror Job status, Session
/// activity, transcript, or Runner state — those live on the
/// participant aggregates.
/// </summary>
[GenerateSerializer]
public sealed record AgentLaunchCoordinatorPlan(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string IdempotencyKey,
    [property: Id(2)] string RequestFingerprint,
    [property: Id(3)] string JobKey,
    [property: Id(4)] string SessionId,
    [property: Id(5)] string InputId,
    [property: Id(6)] string TurnId,
    [property: Id(7)] string AgentId,
    [property: Id(8)] string AgentName,
    [property: Id(9)] string? AgentInstructions,
    [property: Id(10)] string? AgentConfigJson,
    [property: Id(11)] string? Model,
    [property: Id(12)] string? Variant,
    [property: Id(13)] string? Runtime,
    [property: Id(14)] string Prompt,
    [property: Id(15)] string? WorkspacePath,
    [property: Id(16)] int? IssueNumber,
    [property: Id(17)] int? EpicNumber,
    [property: Id(18)] string? Repository,
    [property: Id(19)] string? Title,
    [property: Id(20)] string? AgentRef,
    [property: Id(21)] bool Completed,
    [property: Id(22)] AgentLaunchCoordinatorPending? Pending = null);

/// <summary>
/// Canonical request payload captured from the launch route. The
/// coordinator computes the fingerprint from this snapshot so a
/// replay with the same request returns the same plan; a replay
/// with different content raises
/// <see cref="LaunchIdempotencyConflictException"/>.
/// </summary>
[GenerateSerializer]
public sealed record AgentLaunchCoordinatorRequest(
    [property: Id(0)] string Prompt,
    [property: Id(1)] string? AgentRef,
    [property: Id(2)] string? Runtime,
    [property: Id(3)] string? WorkspacePath,
    [property: Id(4)] int? IssueNumber,
    [property: Id(5)] int? EpicNumber,
    [property: Id(6)] string? Repository,
    [property: Id(7)] string? Title);

/// <summary>
/// Result returned by the coordinator on success. Carries the four
/// stable launch references the 201 response surfaces and the
/// observation URL (relative to the launch's project).
/// </summary>
[GenerateSerializer]
public sealed record AgentLaunchCoordinatorResult(
    [property: Id(0)] string JobKey,
    [property: Id(1)] string SessionId,
    [property: Id(2)] string InputId,
    [property: Id(3)] string TurnId,
    [property: Id(4)] string AgentId,
    [property: Id(5)] string AgentName,
    [property: Id(6)] bool AlreadyPersisted);

/// <summary>
/// Raised when the supplied idempotency key has already accepted a
/// launch whose canonical request fingerprint differs from the
/// replay. The route translates this to a 409 with the
/// <c>launch_idempotency_conflict</c> code.
/// </summary>
public sealed class LaunchIdempotencyConflictException : Exception
{
    public LaunchIdempotencyConflictException(string idempotencyKey, string existingFingerprint)
        : base($"Idempotency-Key '{idempotencyKey}' has already accepted a different launch request.")
    {
        IdempotencyKey = idempotencyKey;
        ExistingFingerprint = existingFingerprint;
    }

    public string IdempotencyKey { get; }
    public string ExistingFingerprint { get; }

    [Orleans.GenerateSerializer]
    public sealed record Serialized(string IdempotencyKey, string ExistingFingerprint);
}

public static class AgentLaunchCoordinatorCodec
{
    /// <summary>
    /// Reversible public key codec for the (ProjectId, IdempotencyKey)
    /// pair. The opaque key shape is the durable grain id; tests and
    /// callers outside the coordinator resolve it through this helper
    /// so the format is owned in one place.
    /// </summary>
    public static string KeyFor(string projectId, string idempotencyKey) =>
        $"agent-launch-coord/{projectId}/{Normalize(idempotencyKey)}";

    /// <summary>
    /// Stable fingerprint the coordinator compares replays against.
    /// Canonicalises the request by sorting optional fields and
    /// trimming whitespace so two clients sending the same logical
    /// intent get the same fingerprint.
    /// </summary>
    public static string Fingerprint(AgentLaunchCoordinatorRequest request)
    {
        var canonical = string.Join('\u001f',
            request.Prompt?.Trim() ?? string.Empty,
            request.AgentRef?.Trim() ?? string.Empty,
            request.Runtime?.Trim() ?? string.Empty,
            request.WorkspacePath?.Trim() ?? string.Empty,
            request.IssueNumber?.ToString() ?? string.Empty,
            request.EpicNumber?.ToString() ?? string.Empty,
            request.Repository?.Trim() ?? string.Empty,
            request.Title?.Trim() ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Normalize(string idempotencyKey)
    {
        var trimmed = idempotencyKey.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Idempotency-Key is required.", nameof(idempotencyKey));
        return trimmed;
    }
}
