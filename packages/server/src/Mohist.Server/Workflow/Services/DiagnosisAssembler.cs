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
    IReadOnlyList<DiagnosisEventView> Events);

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
    JsonElement? Snapshot = null);

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

        var workId = selectedTask?.WorkId;
        if (workId is null)
            workId = stage?.TerminalChecksWorkId ?? stage?.ChecksWorkId;

        var snapshotJson = workId is null
            ? null
            : await _snapshots.LoadJsonAsync(run.Id, workId, ct);
        var snapshot = ParseSnapshot(snapshotJson);
        var events = await _events.ListWorkflowEventsAsync(run.Id, Math.Max(0, eventLimit), ct);
        var tasks = stage is null
            ? []
            : stage.Tasks
                .OrderByDescending(task => failure?.TaskId is not null && ReferenceEquals(task, selectedTask))
                .Select(task => ToTask(task, run, snapshotJson, selectedTask))
                .ToList();

        return new DiagnosisView(
            run.Id,
            run.Status.ToString(),
            failure is null ? null : new FailureStatusView(
                failure.Reason.ToString(),
                failure.Stage,
                failure.TaskId,
                failure.CheckName,
                failure.Message,
                failure.Error),
            tasks,
            snapshotJson is null
                ? new DiagnosisDispatchView("missing")
                : new DiagnosisDispatchView("present", SanitizeJson(snapshot)),
            events.Select(ToEvent).ToList());
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

        return new DiagnosisView(
            run.Id,
            run.Status.ToString(),
            failure is null ? null : new FailureStatusView(failure.Reason.ToString(), failure.Stage, failure.TaskId, failure.CheckName, failure.Message, failure.Error),
            (stage?.Tasks ?? [])
                .OrderByDescending(task => failure?.TaskId is not null && ReferenceEquals(task, selectedTask))
                .Select(task => ToTask(task, run, snapshotJson, selectedTask))
                .ToList(),
            snapshotJson is null
                ? new DiagnosisDispatchView("missing")
                : new DiagnosisDispatchView("present", SanitizeJson(ParseSnapshot(snapshotJson))),
            events.TakeLast(Math.Max(0, eventLimit)).Select(ToEvent).ToList());
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

    private static DiagnosisWorkspaceView WorkspaceOf(WorkflowRun run) =>
        run.Workspace is { Path: { Length: > 0 } workspace
        }
            ? new DiagnosisWorkspaceView(workspace, "named", run.Workspace.Branch ?? WorkflowRunBranch.For(run.Id))
            : new DiagnosisWorkspaceView(null, "fallback", run.Workspace?.Branch ?? WorkflowRunBranch.For(run.Id));

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
