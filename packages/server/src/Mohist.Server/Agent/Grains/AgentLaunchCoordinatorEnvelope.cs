using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Agent.Services;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Public envelope the coordinator route forwards. Carries the
/// canonical request snapshot plus the resolved Agent fields the
/// coordinator needs to populate the plan and the metadata the
/// AgentSession will receive.
/// </summary>
[GenerateSerializer]
public sealed record AgentLaunchCoordinatorCommandEnvelope(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string IdempotencyKey,
    [property: Id(2)] string AgentId,
    [property: Id(3)] string AgentName,
    [property: Id(4)] string? AgentInstructions,
    [property: Id(5)] string? AgentConfigJson,
    [property: Id(6)] string? Model,
    [property: Id(7)] string? Variant,
    [property: Id(8)] string? Runtime,
    [property: Id(9)] string Prompt,
    [property: Id(10)] string? WorkspacePath,
    [property: Id(11)] int? IssueNumber,
    [property: Id(12)] int? EpicNumber,
    [property: Id(13)] string? Repository,
    [property: Id(14)] string? Title,
    [property: Id(15)] AgentLaunchCoordinatorRequest Request = null!,
    [property: Id(16)] ConnectionLaunchOrigin? ConnectionOrigin = null,
    /// <summary>
    /// Pre-minted input id the route wants the coordinator to use.
    /// When non-null the coordinator adopts this id verbatim
    /// instead of minting a fresh one. Required when the launch
    /// carries attachments so the route can validate+bind them
    /// before the plan is committed (binding keys on the input id).
    /// Append-only Orleans field id (next free after
    /// <see cref="ConnectionOrigin"/>).
    /// </summary>
    [property: Id(17)] string? PreMintedInputId = null,
    /// <summary>
    /// Pre-minted turn id the route wants the coordinator to use.
    /// Mirrors <see cref="PreMintedInputId"/>: when non-null the
    /// coordinator adopts this id verbatim. Append-only Orleans
    /// field id (next free after <see cref="PreMintedInputId"/>).
    /// </summary>
    [property: Id(18)] string? PreMintedTurnId = null,
    /// <summary>
    /// Accepted attachment descriptors the route already bound to
    /// <see cref="PreMintedInputId"/>. Persisted on the durable
    /// plan so recovery replays the same accepted set; the
    /// AgentSession initial-launch and AgentJob dispatch builders
    /// project these onto the durable SessionInput child record
    /// and the AgentJob dispatch envelope. Append-only Orleans
    /// field id (next free after <see cref="PreMintedTurnId"/>).
    /// </summary>
    [property: Id(19)] IReadOnlyList<AgentSessionInputAttachmentDescriptor>? Attachments = null,
    [property: Id(20)] string? PreMintedSessionId = null,
    /// <summary>
    /// Optional bounded external discussion the caller attaches to
    /// the first launch as read-only background. Carried verbatim
    /// onto the durable plan (<see cref="StartupContext"/>) so a
    /// recovery replay returns the first-accepted snapshot rather
    /// than recomputing it. Composed at dispatch time as an
    /// explicit read-only block prepended to the task prompt;
    /// <see cref="AgentJobInput.Prompt"/> and the SessionInput text
    /// stay task-only so the work label stays clean. Append-only
    /// Orleans field id (next free after
    /// <see cref="PreMintedSessionId"/>).
    /// </summary>
    [property: Id(21)] AgentStartupContext? StartupContext = null,
    [property: Id(22)] AllowedSubagentSnapshot[]? AllowedSubagents = null,
    [property: Id(23)] string? PinnedRunnerId = null,
    [property: Id(24)] AgentSessionStartup? AgentSessionStartup = null,
    [property: Id(25)] string? ParentSessionId = null,
    [property: Id(26)] string? ParentAgentId = null,
    [property: Id(27)] string? ParentExpectedWorkDir = null,
    [property: Id(28)] string? ParentExpectedRunnerId = null,
    [property: Id(29)] string? ParentExpectedRuntime = null,
    [property: Id(30)] string? ParentExpectedRuntimeSessionId = null,
    [property: Id(31)] string? ParentLinkEdgeId = null,
    [property: Id(32)] string? SpawnRequestFingerprint = null,
    [property: Id(33)] long? ParentExpectedBindingEpoch = null,
    [property: Id(34)] string? WorkspaceName = null,
    [property: Id(35)] IReadOnlyList<WorkspaceRepositorySnapshot>? WorkspaceRepositories = null,
    [property: Id(36)] string? Origin = null,
    [property: Id(37)] string? TargetId = null,
    [property: Id(38)] string? ReasoningEffort = null,
    [property: Id(39)] string? ExecutionOverrideJson = null);
