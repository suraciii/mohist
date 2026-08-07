using System.Security.Cryptography;
using System.Text;
using Mohist.Server.Contracts;
using Mohist.Server.Agent.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Participant command kinds the coordinator advances through. The
/// coordinator persists at most one pending command at a time so a
/// recovery reminder resumes the same step the partial run left
/// behind.
/// </summary>
public enum AgentLaunchCoordinatorCommand
{
    None = 0,
    PrepareJob = 1,
    EnsureInitialLaunch = 2,
    SubmitJob = 3,
    ReserveLink = 4,
    EnsureParentLink = 5,
    AbortLaunch = 6,
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
    [property: Id(15)] string? WorkspaceName,
    [property: Id(16)] int? IssueNumber,
    [property: Id(17)] int? EpicNumber,
    [property: Id(18)] string? Repository,
    [property: Id(19)] string? Title,
    [property: Id(20)] string? AgentRef,
    [property: Id(21)] bool Completed,
    [property: Id(22)] AgentLaunchCoordinatorPending? Pending = null,
    [property: Id(23)] ConnectionLaunchOrigin? ConnectionOrigin = null,
    /// <summary>
    /// Accepted attachment descriptors the route already bound to
    /// <see cref="InputId"/>. Persisted on the plan so recovery
    /// replays the same set the first delivery accepted; the
    /// AgentSession initial-launch and AgentJob dispatch builders
    /// project these onto the durable SessionInput child record and
    /// the AgentJob dispatch envelope. Append-only Orleans field id
    /// (next free after <see cref="ConnectionOrigin"/>).
    /// </summary>
    [property: Id(24)] IReadOnlyList<AgentSessionInputAttachmentDescriptor>? Attachments = null,
    /// <summary>
    /// Optional bounded external discussion the caller attaches to
    /// the first launch as read-only background. Persisted on the
    /// plan so a recovery replay returns the original snapshot
    /// rather than recomputing it (the background is volatile by
    /// definition; the first-accepted snapshot is the authoritative
    /// source of truth on replays). Composed into the dispatched
    /// agent input as an explicit read-only block prepended to the
    /// task prompt at <c>BuildDispatch</c> time; never folded into
    /// <see cref="RequestFingerprint"/> (background is a volatile
    /// snapshot, unlike <see cref="Attachments"/> which the caller
    /// validates and binds before launch). Null for launches that
    /// carry no startup context — a launch that omits it is
    /// observationally identical to before this capability existed.
    /// Append-only Orleans field id (next free after
    /// <see cref="Attachments"/>).
    /// </summary>
    [property: Id(25)] AgentStartupContext? StartupContext = null,
    [property: Id(26)] AllowedSubagentSnapshot[]? AllowedSubagents = null,
    [property: Id(27)] string? PinnedRunnerId = null,
    [property: Id(28)] AgentSessionStartup? AgentSessionStartup = null,
    [property: Id(29)] string? ParentSessionId = null,
    [property: Id(30)] string? ParentAgentId = null,
    [property: Id(31)] string? ParentExpectedWorkDir = null,
    [property: Id(32)] string? ParentExpectedRunnerId = null,
    [property: Id(33)] string? ParentExpectedRuntime = null,
    [property: Id(34)] string? ParentExpectedRuntimeSessionId = null,
    [property: Id(35)] string? ParentLinkEdgeId = null,
    [property: Id(36)] long? ParentLinkRevision = null,
    [property: Id(37)] string? RejectionReason = null,
    [property: Id(38)] bool AbortFenceAcknowledged = false,
    [property: Id(39)] bool AbortJobAcknowledged = false,
    [property: Id(40)] bool AbortSessionAcknowledged = false,
    [property: Id(41)] string? SpawnRequestFingerprint = null,
    [property: Id(42)] bool PostPlanRejected = false,
    [property: Id(43)] long? ParentExpectedBindingEpoch = null,
    [property: Id(44)] SessionTreeBindingUseReceipt? ParentBindingUseReceipt = null,
    [property: Id(45)] bool ParentBindingReleased = false,
    [property: Id(46)] bool AbortParentBindingAcknowledged = false,
    [property: Id(47)] string? WorkspacePath = null,
    [property: Id(48)] IReadOnlyList<WorkspaceRepositorySnapshot>? WorkspaceRepositories = null);

/// <summary>
/// Canonical request payload captured from the launch route. The
/// coordinator computes the fingerprint from this snapshot so a
/// replay with the same request returns the same plan; a replay
/// with different content raises
/// <see cref="LaunchIdempotencyConflictException"/>.
/// </summary>
/// <param name="AttachmentIds">
/// Ordered, caller-supplied attachment ids the route already
/// validated and bound to the launch-time input id. Empty/null
/// when the launch carries no attachments. The fingerprint folds
/// these into the canonical hash so a replay with a different
/// attachment set is rejected as a conflicting idempotency replay.
/// Append-only Orleans field id (next free after <see cref="Title"/>).
/// </param>
[GenerateSerializer]
public sealed record AgentLaunchCoordinatorRequest(
    [property: Id(0)] string Prompt,
    [property: Id(1)] string? AgentRef,
    [property: Id(2)] string? Runtime,
    [property: Id(3)] string? WorkspacePath,
    [property: Id(4)] int? IssueNumber,
    [property: Id(5)] int? EpicNumber,
    [property: Id(6)] string? Repository,
    [property: Id(7)] string? Title,
    [property: Id(8)] IReadOnlyList<string>? AttachmentIds = null,
    /// <summary>
    /// Optional bounded external discussion the caller attaches as
    /// first-launch-only background. Carried on the request so it
    /// threads through the launch chain, but
    /// <strong>deliberately excluded</strong> from
    /// <see cref="AgentLaunchCoordinatorCodec.Fingerprint"/>: the
    /// background is a volatile snapshot, the mention-message
    /// identity is the dedup boundary, and a recovery replay must
    /// return the first-accepted snapshot rather than conflict on
    /// history drift. A request that omits this field is
    /// observationally identical to a pre-capability launch (same
    /// fingerprint, same dispatched prompt, same session-input text).
    /// Append-only Orleans field id (next free after
    /// <see cref="AttachmentIds"/>).
    /// </summary>
    [property: Id(9)] AgentStartupContext? StartupContext = null,
    [property: Id(10)] bool ExactPromptFingerprint = false,
    [property: Id(11)] string? WorkspaceName = null,
    /// <summary>
    /// Pre-resolved workspace repository list. When set (because
    /// <see cref="WorkspaceName"/> was supplied), this snapshot
    /// carries the repository name and GitUrl for every repository
    /// member of the named workspace at launch time. The AgentJob
    /// grain reads this instead of calling IProjectGrain so the
    /// Agent domain does not depend on Project. Null when the
    /// launch did not bind a workspace or the workspace has no
    /// repositories. Append-only Orleans field id (next free after
    /// <see cref="WorkspaceName"/>).
    /// </summary>
    [property: Id(12)] IReadOnlyList<WorkspaceRepositorySnapshot>? WorkspaceRepositories = null);

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
    [property: Id(6)] bool AlreadyPersisted,
    [property: Id(7)] string? ParentLinkEdgeId = null);

/// <summary>
/// Raised when the supplied idempotency key has already accepted a
/// launch whose canonical request fingerprint differs from the
/// replay. The route translates this to a 409 with the
/// <c>launch_idempotency_conflict</c> code.
/// </summary>
[Serializable]
[Orleans.GenerateSerializer]
public sealed class LaunchIdempotencyConflictException : Exception
{
    public LaunchIdempotencyConflictException(string idempotencyKey, string existingFingerprint)
        : base($"Idempotency-Key '{idempotencyKey}' has already accepted a different launch request.")
    {
        IdempotencyKey = idempotencyKey;
        ExistingFingerprint = existingFingerprint;
    }

    [Orleans.Id(0)]
    public string IdempotencyKey { get; }
    [Orleans.Id(1)]
    public string ExistingFingerprint { get; }

    [Orleans.GenerateSerializer]
    public sealed record Serialized(string IdempotencyKey, string ExistingFingerprint);
}

[Serializable]
[Orleans.GenerateSerializer]
public sealed class LaunchSetupPendingException : Exception
{
    public LaunchSetupPendingException(string idempotencyKey)
        : base("Agent launch setup is still recovering. Retry with the original Idempotency-Key.")
    {
        IdempotencyKey = idempotencyKey;
    }

    [Orleans.Id(0)]
    public string IdempotencyKey { get; }
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

    public static string KeyFor(string projectId, string parentSessionId, string idempotencyKey) =>
        $"agent-launch-coord/{projectId}/{parentSessionId}/{Normalize(idempotencyKey)}";

    public static string StableToken(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>
    /// Stable fingerprint the coordinator compares replays against.
    /// Canonicalises target and optional reference fields according to
    /// their command contracts. Spawn prompts can opt into exact
    /// string identity because whitespace is part of the request.
    /// </summary>
    /// <remarks>
    /// The fingerprint <strong>deliberately excludes</strong>
    /// <see cref="AgentLaunchCoordinatorRequest.StartupContext"/>:
    /// the background is a volatile snapshot read at processing
    /// time, so two launches that differ only in background must
    /// hash to the same fingerprint. The dedup boundary is the
    /// mention-message identity (or the caller-supplied
    /// <c>Idempotency-Key</c>) — a plain redelivery never reaches
    /// the coordinator with drifted content because the provider
    /// inbox dedups on <c>(ConnectionId, SlackMessageIdentity)</c>.
    /// For recovery/replay robustness the first-accepted snapshot
    /// is persisted on the plan rather than re-deriving equality
    /// from volatile content.
    /// </remarks>
    public static string Fingerprint(AgentLaunchCoordinatorRequest request)
        => Fingerprint(request, null);

    public static string Fingerprint(
        AgentLaunchCoordinatorRequest request,
        ConnectionLaunchOrigin? connectionOrigin)
    {
        var attachments = request.AttachmentIds is null
            ? string.Empty
            : string.Join('\u001e', request.AttachmentIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim()));
        var canonical = string.Join('\u001f',
            request.ExactPromptFingerprint
                ? request.Prompt ?? string.Empty
                : request.Prompt?.Trim() ?? string.Empty,
            request.AgentRef?.Trim() ?? string.Empty,
            request.Runtime?.Trim() ?? string.Empty,
            request.WorkspaceName?.Trim() ?? string.Empty,
            request.WorkspacePath?.Trim() ?? string.Empty,
            request.IssueNumber?.ToString() ?? string.Empty,
            request.EpicNumber?.ToString() ?? string.Empty,
            request.Repository?.Trim() ?? string.Empty,
            request.Title?.Trim() ?? string.Empty,
            attachments,
            connectionOrigin?.ConnectionId ?? string.Empty,
            connectionOrigin?.WorkspaceTeamId ?? string.Empty,
            connectionOrigin?.SlackUserId ?? string.Empty,
            connectionOrigin?.ConversationId ?? string.Empty,
            connectionOrigin?.MessageTs ?? string.Empty,
            connectionOrigin?.ThreadTs ?? string.Empty);
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

    public static string SpawnFingerprint(string targetAgentRef, string prompt)
    {
        var canonical = $"{targetAgentRef.Trim()}\u001f{prompt}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
