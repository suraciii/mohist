using System.Text.Json.Serialization;

namespace Mohist.Server.Runner.Services;

public sealed record RunnerStatusView(
    string Id,
    string Kind,
    string Hostname,
    RunnerScopeView Scope,
    string Status,
    DateTimeOffset? RegisteredAt,
    DateTimeOffset? LastHeartbeatAt,
    string? ConnectionState,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> CoderModels,
    int CoderModelCount,
    RunnerCapacityView? Capacity,
    IReadOnlyList<RunnerActiveWorkView> ActiveWorks,
    string? BuildGitHash = null);

public sealed record RunnerScopeView(string Type);

public sealed record RunnerCapacityView(
    int UsedSlots,
    int TotalSlots);

public sealed record RunnerActiveWorkView(
    string WorkId,
    string OwnerKind,
    string OwnerId,
    string WorkType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Stage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] RunnerActiveWorkIssueView? Issue = null);

public sealed record RunnerActiveWorkIssueView(
    string ProjectId,
    int IssueNumber);

public sealed record RunnerStatusEntry(
    RunnerIdentityStatusView Identity,
    RunnerPresenceStatusView Presence,
    RunnerControlStatusView Control,
    RunnerAdmissionStatusView Admission,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<RunnerRuntimeStatusView> Runtimes,
    RunnerStatusCapacityView? Capacity,
    IReadOnlyList<RunnerActiveWorkView> ActiveWorks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] RunnerDrainStatusView? Drain,
    IReadOnlyList<RunnerNextActionView> NextActions);

public sealed record RunnerIdentityStatusView(
    string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Hostname,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Component,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SourceRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ReleaseId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Generation);

public sealed record RunnerPresenceStatusView(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastObservedAt);

public sealed record RunnerControlStatusView(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Generation);

public sealed record RunnerAdmissionStatusView(
    string State,
    IReadOnlyList<string> ReasonCodes);

public sealed record RunnerRuntimeStatusView(
    string Name,
    RunnerRuntimeReadinessStatusView Readiness,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] RunnerRuntimeCatalogStatusView? Catalog);

public sealed record RunnerRuntimeReadinessStatusView(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Generation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ReasonCode);

public sealed record RunnerRuntimeCatalogStatusView(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? Complete,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CapabilityRevision,
    int ModelCount,
    IReadOnlyList<string> Models,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Variants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? SupportsReasoningEffort,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ReasoningEfforts);

public sealed record RunnerStatusCapacityView(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Used,
    int Total);

public sealed record RunnerDrainStatusView(
    bool Active,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UpdateInterruptId);

public sealed record RunnerNextActionView(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Command);

public sealed record RunnerStatusListSnapshot(
    DateTimeOffset ObservedAt,
    IReadOnlyList<RunnerStatusEntry> Runners);

public sealed record RunnerStatusDetailSnapshot(
    DateTimeOffset ObservedAt,
    RunnerStatusEntry Runner);
