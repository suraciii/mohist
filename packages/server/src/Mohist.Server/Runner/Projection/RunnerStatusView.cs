namespace Mohist.Server.Runner.Projection;

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
    RunnerActiveWorkView? ActiveWork);

public sealed record RunnerScopeView(
    string Type,
    string? ProjectId = null,
    string? ProjectName = null);

public sealed record RunnerCapacityView(
    int UsedSlots,
    int TotalSlots);

public sealed record RunnerActiveWorkView(
    string WorkId,
    string WorkflowRunId,
    string? WorkType = null,
    string? Stage = null,
    string? Title = null);
