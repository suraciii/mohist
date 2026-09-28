using Mohist.Server.Infrastructure;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

public sealed record DiagnosisView(
    string WorkflowRunId,
    string Status,
    FailureStatusView? Failure,
    IReadOnlyList<DiagnosisTaskView> Tasks,
    DiagnosisDispatchView Dispatch,
    IReadOnlyList<DiagnosisEventView> Events,
    IReadOnlyList<DiagnosisProvenanceFactView>? Provenance = null);

public sealed record DiagnosisProvenanceFactView(
    DateTimeOffset Time,
    string Action,
    string ActorKind,
    string ActorId,
    string Source,
    string Outcome,
    string? Stage,
    string? TaskId,
    int? Attempt,
    string? Result,
    string? Reason,
    string? Target,
    string? WorkflowRunId = null,
    string? ProjectId = null,
    int? IssueNumber = null);

public sealed record DiagnosisTaskView(
    string TaskId,
    int Attempt,
    string? Uses,
    JsonElement? RenderedWith,
    DiagnosisWorkspaceView Workspace,
    int? ExitCode,
    ExecutionError? Error,
    DiagnosisRecoveryView Recovery);

public sealed record DiagnosisWorkspaceView(
    string? Path,
    string Binding,
    string Branch);

public sealed record DiagnosisRecoveryView(
    int? Budget,
    int? Remaining,
    IReadOnlyList<DiagnosisRecoveryHandlerView> Handlers);

public sealed record DiagnosisRecoveryHandlerView(
    string? When,
    bool RetrySelf,
    IReadOnlyList<string> TaskIds);

public sealed record DiagnosisDispatchView(
    string Status,
    JsonElement? Snapshot = null,
    DiagnosisActiveWorkView? ActiveWork = null);

/// <summary>
/// The Run's persisted in-flight work, read from the same run-state fields
/// the Runner status projection (<c>RunnerActiveWorkReader</c>) uses for
/// activeWorks: the current stage's running task (effective work id
/// <c>WorkId ?? Id</c>) or the stage's checks work id. It is an observation
/// of persisted state, never a fabricated dispatch snapshot.
/// <c>MatchesSnapshotWorkId</c> separates "the snapshot row for the live
/// work is absent" from "the snapshot lookup key and the live work
/// diverged"; when both the snapshot and the active work are absent the
/// dispatch is truly missing and this view is null.
/// </summary>
public sealed record DiagnosisActiveWorkView(
    string WorkId,
    string WorkType,
    string Stage,
    bool MatchesSnapshotWorkId);

public sealed record DiagnosisEventView(
    long Id,
    string EventId,
    string Source,
    string Type,
    string SpecVersion,
    string? Subject,
    DateTimeOffset Time,
    string? DataContentType,
    JsonElement? Data,
    IReadOnlyDictionary<string, string> Extensions);

public sealed class DiagnosisAssembler
{
    public const int DefaultEventLimit = 200;

    private readonly WorkflowRunQuerier _runs;
    private readonly WorkflowEventQuerier _events;
    private readonly IDispatchSnapshotStore _snapshots;

    public DiagnosisAssembler(
        WorkflowRunQuerier runs,
        WorkflowEventQuerier events,
        IDispatchSnapshotStore snapshots)
    {
        _runs = runs;
        _events = events;
        _snapshots = snapshots;
    }

    public async Task<DiagnosisView?> AssembleAsync(
        string workflowRunId,
        int eventLimit = DefaultEventLimit,
        CancellationToken ct = default)
    {
        var run = await _runs.LoadAsync(workflowRunId, ct);
        if (run is null) return null;

        var failure = run.EffectiveFailure();
        var stage = failure is not null
            ? run.Stages.FirstOrDefault(s => string.Equals(s.Id, failure.Stage, StringComparison.Ordinal))
            : null;
        stage ??= run.Stages.FirstOrDefault(s => string.Equals(s.Id, run.CurrentStageId, StringComparison.Ordinal));
        var selectedTask = SelectTask(stage, failure);
        var workId = SnapshotWorkId(stage, selectedTask);

        var snapshotJson = workId is null
            ? null
            : await _snapshots.LoadJsonAsync(run.Id, workId, ct);
        var events = await _events.ListValidWorkflowEventsAsync(run.Id, ct);

        return Assemble(run, snapshotJson, events, eventLimit);
    }

    public static DiagnosisView Assemble(
        WorkflowRun run,
        string? snapshotJson,
        IReadOnlyList<StoredCloudEvent> events,
        int eventLimit = DefaultEventLimit)
    {
        var failure = run.EffectiveFailure();
        var stage = failure is not null
            ? run.Stages.FirstOrDefault(s => string.Equals(s.Id, failure.Stage, StringComparison.Ordinal))
            : null;
        stage ??= run.Stages.FirstOrDefault(s => string.Equals(s.Id, run.CurrentStageId, StringComparison.Ordinal));
        var selectedTask = SelectTask(stage, failure);
        var workId = SnapshotWorkId(stage, selectedTask);
        var activeWork = PersistedActiveWork(run, workId);

        return new DiagnosisView(
            run.Id,
            run.Status.ToString(),
            failure is null ? null : new FailureStatusView(failure.Reason.ToString(), failure.Stage, failure.TaskId, failure.CheckName, failure.Message, failure.Error),
            (stage?.Tasks ?? [])
                .OrderByDescending(task => failure?.TaskId is not null && ReferenceEquals(task, selectedTask))
                .Select(task => ToTask(task, run, snapshotJson, selectedTask))
                .ToList(),
            snapshotJson is null
                ? new DiagnosisDispatchView("missing", ActiveWork: activeWork)
                : new DiagnosisDispatchView("present", SanitizeJson(ParseSnapshot(snapshotJson)), activeWork),
            events.TakeLast(Math.Max(0, eventLimit)).Select(ToEvent).ToList(),
            events.Select(stored => ToProvenance(stored, run.Id)).Where(fact => fact is not null).Cast<DiagnosisProvenanceFactView>().ToList());
    }

    private static string? SnapshotWorkId(StageRun? stage, WorkflowActionAttempt? selectedTask)
    {
        var workId = selectedTask is { Status: WorkflowActionAttemptStatus.Running } running
            ? running.WorkId ?? running.Id
            : selectedTask?.WorkId;
        if (workId is null)
            workId = stage?.TerminalChecksWorkId ?? stage?.ChecksWorkId;
        return workId;
    }

    // PersistedActiveWork reports the in-flight work the Run state persists,
    // using the same fields as the Runner status projection
    // (RunnerActiveWorkReader): the current stage's running task with its
    // effective work id (WorkId ?? Id), otherwise the stage's checks work
    // id. It never fabricates a dispatch snapshot; it only tells whether
    // persisted active work exists and whether that work is the one the
    // snapshot lookup used.
    private static DiagnosisActiveWorkView? PersistedActiveWork(WorkflowRun run, string? snapshotWorkId)
    {
        var current = run.Stages.FirstOrDefault(s => string.Equals(s.Id, run.CurrentStageId, StringComparison.Ordinal));
        if (current is null) return null;

        if (current.RunningTask is { } task)
        {
            var workId = task.WorkId ?? task.Id;
            return new DiagnosisActiveWorkView(workId, "task", current.Id, Matches(workId, snapshotWorkId));
        }
        if (!string.IsNullOrWhiteSpace(current.ChecksWorkId))
        {
            return new DiagnosisActiveWorkView(current.ChecksWorkId, "checks", current.Id, Matches(current.ChecksWorkId, snapshotWorkId));
        }
        return null;

        static bool Matches(string activeWorkId, string? snapshotWorkId) =>
            snapshotWorkId is not null && string.Equals(activeWorkId, snapshotWorkId, StringComparison.Ordinal);
    }

    private static DiagnosisTaskView ToTask(
        WorkflowActionAttempt task,
        WorkflowRun run,
        string? snapshotJson = null,
        WorkflowActionAttempt? selectedTask = null)
    {
        var renderedWith = ReferenceEquals(task, selectedTask)
            ? RenderedWith(snapshotJson, task.WithInput)
            : JsonElementFrom(task.WithInput);
        var recovery = task.Recovery;
        return new DiagnosisTaskView(
            task.Id,
            task.Attempt,
            task.Uses,
            renderedWith,
            WorkspaceOf(run),
            ReadExitCode(task.Output),
            task.Error,
            new DiagnosisRecoveryView(
                recovery?.Budget,
                task.RecoveryRemaining,
                recovery?.Handlers.Select(handler => new DiagnosisRecoveryHandlerView(
                    handler.When,
                    handler.RetrySelf,
                    handler.Tasks.Select(t => t.Id).ToList())).ToList() ?? []));
    }

    private static WorkflowActionAttempt? SelectTask(StageRun? stage, FailureDetails? failure)
    {
        if (stage is null) return null;
        if (failure?.TaskId is { } taskId)
            return stage.Tasks.FirstOrDefault(task =>
                string.Equals(task.Id, taskId, StringComparison.Ordinal)
                || string.Equals(task.DefinitionId, taskId, StringComparison.Ordinal));
        return stage.Tasks.FirstOrDefault(task => task.Status == WorkflowActionAttemptStatus.Running);
    }

    private static DiagnosisWorkspaceView WorkspaceOf(WorkflowRun run)
    {
        if (run.Metadata.IssueNumber is > 0 and var issueNumber)
        {
            return new DiagnosisWorkspaceView(
                run.Workspace?.Path is { Length: > 0 } path ? path : null,
                "named",
                run.Workspace?.Branch ?? $"mohist/ws-issue-{issueNumber}");
        }

        return run.Workspace is { Path: { Length: > 0 } workspace }
            ? new DiagnosisWorkspaceView(workspace, "named", run.Workspace.Branch ?? string.Empty)
            : new DiagnosisWorkspaceView(null, "fallback", run.Workspace?.Branch ?? string.Empty);
    }

    private static int? ReadExitCode(JsonElement? output)
    {
        if (output is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("exitCode", out var code)
            || !code.TryGetInt32(out var result))
            return null;
        return result;
    }

    private static JsonElement? RenderedWith(string? snapshotJson, Dictionary<string, JsonElement?>? fallback)
    {
        var snapshot = ParseSnapshot(snapshotJson);
        if (snapshot is { ValueKind: JsonValueKind.Object }
            && TryGetProperty(snapshot.Value, "with", out var with)
            && with.ValueKind == JsonValueKind.String
            && with.GetString() is { } rendered)
        {
            try { return SanitizeJson(JsonDocument.Parse(rendered).RootElement); }
            catch (JsonException) { }
        }
        return JsonElementFrom(fallback);
    }

    private static JsonElement? JsonElementFrom(Dictionary<string, JsonElement?>? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value);

    private static JsonElement? ParseSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static DiagnosisProvenanceFactView? ToProvenance(StoredCloudEvent stored, string runId)
    {
        if (!string.Equals(
                stored.Envelope.Type,
                EventCatalog.ReverseDns.WorkflowProvenanceRecorded,
                StringComparison.Ordinal)
            || stored.Envelope.Data is not { } data)
            return null;

        try
        {
            var fact = data.Deserialize<WorkflowProvenanceRecorded>(JSON.Options);
            if (fact is null) return null;
            var extensions = stored.Envelope.Extensions;
            var projectId = extensions.TryGetValue(EventCatalog.Lineage.ProjectId, out var project)
                ? project
                : null;
            var issueNumber = extensions.TryGetValue(EventCatalog.Lineage.Issue, out var issue)
                && int.TryParse(issue, out var parsedIssue)
                ? (int?)parsedIssue
                : null;
            var lineageRunId = extensions.TryGetValue(EventCatalog.Lineage.WorkflowRunId, out var eventRunId)
                ? eventRunId
                : runId;
            return new DiagnosisProvenanceFactView(
                stored.Envelope.Time,
                fact.Action,
                fact.ActorKind,
                fact.ActorId,
                fact.Source,
                fact.Outcome,
                fact.Stage,
                fact.TaskId,
                fact.Attempt,
                fact.Result,
                fact.Reason,
                fact.Target,
                lineageRunId,
                projectId,
                issueNumber);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static DiagnosisEventView ToEvent(StoredCloudEvent stored) => new(
        stored.Id,
        stored.Envelope.Id,
        stored.Envelope.Source.ToString(),
        stored.Envelope.Type,
        stored.Envelope.SpecVersion,
        stored.Envelope.Subject,
        stored.Envelope.Time,
        stored.Envelope.DataContentType,
        SanitizeJson(stored.Envelope.Data),
        new Dictionary<string, string>(stored.Envelope.Extensions));

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static JsonElement? SanitizeJson(JsonElement? value)
    {
        if (value is not { } element) return null;
        var node = JsonNode.Parse(element.GetRawText());
        SanitizeNode(node);
        return node is null ? null : JsonSerializer.SerializeToElement(node);
    }

    private static void SanitizeNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Value is JsonValue stringValue
                    && stringValue.TryGetValue<string>(out var text)
                    && IsProcessScopedPath(text))
                    obj.Remove(property.Key);
                else
                    SanitizeNode(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = array.Count - 1; i >= 0; i--)
            {
                if (array[i] is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && IsProcessScopedPath(text))
                    array.RemoveAt(i);
                else
                    SanitizeNode(array[i]);
            }
        }
    }

    private static bool IsProcessScopedPath(string value) =>
        value.Contains("/proc/", StringComparison.OrdinalIgnoreCase)
        && value.Contains("/fd/", StringComparison.OrdinalIgnoreCase);
}
