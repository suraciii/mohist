using System.Text.Json;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Domain.Run;

public static class WorkItemTypes
{
    public const string Task = "task";
    public const string Checks = "checks";
}

/// <summary>
/// Domain-semantic work item returned by <c>IWorkflowGrain.ClaimNextAsync</c>.
/// Carries declarations and unrendered templates only; the worker-side
/// translator turns it into an executable dispatch.
///
/// The shape mirrors the execution-plane <c>WorkItem</c> so the worker can
/// hydrate its in-process type directly from the JSON returned by the
/// control plane. Polymorphism is encoded as a single sealed record with
/// a <see cref="WorkType"/> discriminator (task / checks) — Orleans
/// requires a concrete serializable type for grain-interface return
/// values, so variants live as nullable fields discriminated by
/// <see cref="WorkType"/> rather than as derived records.
/// </summary>
[GenerateSerializer]
public sealed record WorkItem(
    [property: Id(0)] string Stage,
    [property: Id(1)] string WorkType,
    // ---- task variant ----
    [property: Id(2)] string? Id,
    [property: Id(3)] string? Title,
    [property: Id(4)] string? Uses,
    [property: Id(5)] Dictionary<string, JsonElement?>? With,
    [property: Id(6)] TaskArtifactCapture? Artifacts,
    [property: Id(7)] Dictionary<string, string>? SetVars,
    // ---- checks variant ----
    [property: Id(8)] IReadOnlyList<CheckItem>? Items,
    [property: Id(9)] RecoveryDefinition? Recovery = null,
    [property: Id(10)] int? RecoveryRemaining = null,
    [property: Id(11)] Dictionary<string, JsonElement?>? Expect = null)
{
    public static WorkItem Task(string stage, string id, string title, string? uses,
        Dictionary<string, JsonElement?>? with, TaskArtifactCapture? artifacts = null,
        Dictionary<string, string>? setVars = null, RecoveryDefinition? recovery = null,
        int? recoveryRemaining = null,
        Dictionary<string, JsonElement?>? expect = null)
        => new(stage, WorkItemTypes.Task, id, title, uses, with, artifacts, setVars, Items: null,
            Recovery: recovery, RecoveryRemaining: recoveryRemaining, Expect: expect);

    public static WorkItem Checks(string stage, string workId, IReadOnlyList<CheckItem> items)
        => new(stage, WorkItemTypes.Checks, workId, Title: null, Uses: null, With: null,
            Artifacts: null, SetVars: null, Items: items);

    public bool IsTask => string.Equals(WorkType, WorkItemTypes.Task, StringComparison.Ordinal);
    public bool IsChecks => string.Equals(WorkType, WorkItemTypes.Checks, StringComparison.Ordinal);
}

[GenerateSerializer]
public sealed record TaskReport(
    [property: Id(0)] string WorkId,
    [property: Id(1)] TaskReportStatus Status,
    [property: Id(2)] System.Text.Json.JsonElement? Output,
    [property: Id(3)] IReadOnlyList<ArtifactRef>? Artifacts,
    [property: Id(4)] string? Detail = null,
    [property: Id(5)] IReadOnlyList<RuntimeTaskInput>? AddTasks = null,
    [property: Id(6)] ExecutionError? Error = null,
    [property: Id(7)] IReadOnlyList<string>? ArtifactUploadIds = null);

[GenerateSerializer]
public sealed record CheckReport(
    [property: Id(0)] string Stage,
    [property: Id(1)] IReadOnlyList<CheckResult> Results);

public enum TaskReportStatus
{
    Succeeded,
    Failed,
}

/// <summary>
/// Reference to an artifact after the Workflow grain has bound its upload.
/// </summary>
[GenerateSerializer]
public sealed record ArtifactRef(
    [property: Id(0)] string Path,
    [property: Id(1)] string? ContentType = null,
    [property: Id(2)] string? ContentHash = null,
    [property: Id(3)] long? Size = null,
    [property: Id(4)] string? UploadId = null);
