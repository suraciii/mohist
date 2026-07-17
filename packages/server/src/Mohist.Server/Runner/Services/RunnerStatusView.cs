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
    string? Stage = null,
    string? Title = null,
    RunnerActiveWorkIssueView? Issue = null);

public sealed record RunnerActiveWorkIssueView(
    string ProjectId,
    int IssueNumber);
