using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;

namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// Discriminator values for the work item variants the control plane
/// surfaces. Only "task" and "checks" are public — stage-init was eagerly
/// absorbed by the InitializeStage pre-commit step (D3) and is no longer
/// visible to callers.
/// </summary>
public static class WorkItemTypes
{
    public const string Task = "task";
    public const string Checks = "checks";
}

/// <summary>
/// Domain-semantic work item returned by <c>IWorkflowGrain.PollWorkAsync</c>.
/// Carries the declaration and unrendered templates only — no dispatch id,
/// no resolved variables, no rendered execution context, no loaded prompts.
/// The caller's translator (RunnerGrain-side WorkflowItemTranslator) is
/// responsible for turning a <see cref="WorkItem"/> into a runner
/// <c>WorkDispatch</c>.
///
/// The shape mirrors the runner TS <c>WorkItem</c> so the runner can
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
    [property: Id(8)] IReadOnlyList<CheckItem>? Items)
{
    public static WorkItem Task(string stage, string id, string title, string? uses,
        Dictionary<string, JsonElement?>? with, TaskArtifactCapture? artifacts = null,
        Dictionary<string, string>? setVars = null)
        => new(stage, WorkItemTypes.Task, id, title, uses, with, artifacts, setVars, Items: null);

    public static WorkItem Checks(string stage, string workId, IReadOnlyList<CheckItem> items)
        => new(stage, WorkItemTypes.Checks, workId, Title: null, Uses: null, With: null,
            Artifacts: null, SetVars: null, Items: items);

    public bool IsTask => string.Equals(WorkType, WorkItemTypes.Task, StringComparison.Ordinal);
    public bool IsChecks => string.Equals(WorkType, WorkItemTypes.Checks, StringComparison.Ordinal);
}

/// <summary>
/// Domain outcome of a task the runner finished. Status is one of
/// <see cref="OutcomeStatus.Passed"/> / <see cref="OutcomeStatus.Failed"/>.
/// Timeouts and runner-loss collapses into <see cref="OutcomeStatus.Failed"/>
/// + <see cref="Detail"/> (e.g. <c>"work-timeout"</c>, <c>"runner-lost"</c>).
/// <see cref="Artifacts"/> are the bound artifact references the runner
/// reported for this task; the grain consumes them to record
/// <c>WorkflowArtifactRecorded</c> events.
/// </summary>
[GenerateSerializer]
public sealed record TaskOutcome(
    [property: Id(0)] string WorkId,
    [property: Id(1)] OutcomeStatus Status,
    [property: Id(2)] string? Output,
    [property: Id(3)] IReadOnlyList<ArtifactRef>? Artifacts,
    [property: Id(4)] string? Detail = null);

/// <summary>
/// Domain outcome of a checks batch. Each <see cref="CheckResult"/> carries
/// the canonical name, status, message, and optional output — the runner
/// translator converts the raw <c>WorkResult.Output</c> JSON into this
/// shape before the grain sees it.
/// </summary>
[GenerateSerializer]
public sealed record CheckOutcome(
    [property: Id(0)] string Stage,
    [property: Id(1)] IReadOnlyList<CheckResult> Results);

public enum OutcomeStatus
{
    Passed,
    Failed,
}

/// <summary>
/// Reference to a bound artifact as reported by the runner-side translator.
/// The grain stores the recorded event without touching the upload pipeline
/// — uploads were resolved on the runner side via
/// <c>IWorkflowArtifactBindService</c>.
/// </summary>
[GenerateSerializer]
public sealed record ArtifactRef(
    [property: Id(0)] string Path,
    [property: Id(1)] string? ContentType = null,
    [property: Id(2)] string? ContentHash = null,
    [property: Id(3)] long? Size = null,
    [property: Id(4)] string? UploadId = null);