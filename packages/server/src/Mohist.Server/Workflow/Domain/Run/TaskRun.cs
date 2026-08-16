using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// The lifecycle status of a <see cref="TaskRun"/> aggregate.
/// This state machine is independent from <see cref="WorkflowRunStatus"/> —
/// the two describe different aggregates and do not derive each other.
/// A task transition between <c>Pending</c>, <c>Running</c>, and <c>Completed</c>
/// does not recompute the workflow status. The two facts may diverge
/// (e.g. a <c>WorkflowRun</c> may be <c>Running</c> while no <c>TaskRun</c>
/// is <c>Running</c>).
/// </summary>
public enum TaskRunStatus { Pending, Running, Completed, Failed, Cancelled }

public sealed class TaskRun
{
    public required string Id { get; init; }
    public required string DefinitionId { get; init; }
    public required int Attempt { get; init; }
    public required string Title { get; init; }
    public string? Uses { get; init; }
    public Dictionary<string, JsonElement?>? WithInput { get; init; }
    public Dictionary<string, JsonElement?>? ExpectInput { get; init; }
    public TaskRunStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? WorkerId { get; set; }
    public string? WorkId { get; set; }
    public TerminalLogOwnership? TerminalLogOwnership { get; set; }
    public AgentResultSettlement? AgentResultSettlement { get; set; }
    public WorkInterruption? Interruption { get; set; }
    /// <summary>
    /// The immutable runner completion boundary accepted for this attempt.
    /// It remains on terminal tasks so exact report replays can be
    /// acknowledged without reopening or re-projecting the attempt.
    /// </summary>
    public WorkflowTaskCompletionBoundary? CompletionBoundary { get; set; }
    /// <summary>
    /// Dispatch-time execution identity. This is the active admission fence
    /// used when the run-level workspace snapshot is intentionally incomplete.
    /// </summary>
    public WorkflowTaskExecutionIdentity? ActiveExecutionIdentity { get; set; }
    /// <summary>
    /// Mutable recovery state for dirty and unconfirmed successful Actions.
    /// Git evidence belongs here only for later observations; the initial
    /// receipt remains in <see cref="CompletionBoundary"/>.
    /// </summary>
    public WorkflowTaskRecovery? WorkflowTaskRecovery { get; set; }
    public WorkflowTaskReportProjection? PendingCompletionReport { get; set; }
    public bool CompletionProjectionApplied { get; set; }
    public IReadOnlyList<WorkflowTaskRequiredFile>? RequiredFiles { get; init; }
    public TaskArtifactCapture? Artifacts { get; init; }
    public Dictionary<string, string>? SetVars { get; init; }
    public RecoveryDefinition? Recovery { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? RecoveryRemaining { get; init; }
    public TaskClassification Classification { get; init; } = TaskClassification.UserFacing;
    public string? CausedByFeedbackId { get; init; }
    public string? CausedByFailedTaskId { get; init; }
    public JsonElement? Output { get; set; }
    public ExecutionError? Error { get; set; }
}

public static class TaskRunExtensions
{
    private const string FilesKey = "files";
    private const string MarkersKey = "markers";
    private const string SessionKey = "session";

    /// <summary>
    /// Marker path sentinel that evaluates the turn's final assistant text
    /// instead of a file (workflow-task-completion spec). The sentinel MUST
    /// NOT be projected as a fetchable file path; required-files evidence
    /// lists only file-backed markers and <c>expect.files</c> paths.
    /// </summary>
    public const string OutputMarkerPath = "_output";

    public static string? ExtractSessionName(Dictionary<string, JsonElement?>? withInput)
    {
        if (withInput is null) return null;
        if (!withInput.TryGetValue(SessionKey, out var session) || !session.HasValue)
            return null;
        return session.Value.ValueKind == JsonValueKind.String
            ? session.Value.GetString()
            : null;
    }

    public static IReadOnlyList<WorkflowTaskRequiredFile> ExtractRequiredFiles(Dictionary<string, JsonElement?>? expectInput)
    {
        if (expectInput is null || expectInput.Count == 0) return [];

        var result = new List<WorkflowTaskRequiredFile>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        if (expectInput.TryGetValue(MarkersKey, out var markerEntries)
            && markerEntries.HasValue
            && markerEntries.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in markerEntries.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                var path = entry.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                if (string.IsNullOrEmpty(path)) continue;
                // `_output` is a turn-text requirement, not a file. Projecting
                // it as a fetchable path would violate the spec scenario
                // "`_output` is not projected as a file".
                if (string.Equals(path, OutputMarkerPath, StringComparison.Ordinal)) continue;

                string[]? oneOf = null;
                if (entry.TryGetProperty("oneOf", out var oneOfEl) && oneOfEl.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var v in oneOfEl.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.String)
                            list.Add(v.GetString()!);
                    oneOf = list.Count > 0 ? list.ToArray() : null;
                }

                string? contains = null;
                if (entry.TryGetProperty("contains", out var containsEl) && containsEl.ValueKind == JsonValueKind.String)
                    contains = containsEl.GetString();

                string? failIf = null;
                if (entry.TryGetProperty("failIf", out var failIfEl) && failIfEl.ValueKind == JsonValueKind.String)
                    failIf = failIfEl.GetString();

                var legacyMarkers = oneOf ?? (contains is not null ? new[] { contains } : null);
                if (seenPaths.Add(path!))
                    result.Add(new WorkflowTaskRequiredFile(path!, "task-expect", CanFetchContent: true, legacyMarkers, oneOf, failIf));
            }
        }

        if (expectInput.TryGetValue(FilesKey, out var files)
            && files.HasValue
            && files.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in files.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var path = item.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                if (string.IsNullOrEmpty(path)) continue;

                string[]? markers = null;
                if (item.TryGetProperty("markers", out var m) && m.ValueKind == JsonValueKind.Array)
                {
                    var markerList = new List<string>();
                    foreach (var marker in m.EnumerateArray())
                        if (marker.ValueKind == JsonValueKind.String)
                            markerList.Add(marker.GetString()!);
                    markers = markerList.Count > 0 ? markerList.ToArray() : null;
                }

                if (seenPaths.Add(path!))
                    result.Add(new WorkflowTaskRequiredFile(path!, "task-expect", CanFetchContent: true, markers));
            }
        }

        return result;
    }

    public static TaskClassification DeriveClassification(string? uses, IReadOnlyList<WorkflowTaskRequiredFile>? requiredFiles)
    {
        if (uses is not null && (uses.StartsWith("core/") || uses.StartsWith("mohist/")) && !uses.Contains("opencode"))
            return TaskClassification.Orchestration;
        return TaskClassification.UserFacing;
    }

    extension(TaskRun)
    {
        internal static TaskRun MakeTask(
            IEnumerable<TaskRun> existing,
            TaskDefinition input,
            int stageAttempt,
            IEnumerable<TaskRun> occupiedTaskRuns,
            string? causedByFeedbackId = null,
            string? causedByFailedTaskId = null)
            => MakeTask(
                existing,
                input,
                stageAttempt,
                recoveryRemaining: null,
                occupiedTaskRuns,
                causedByFeedbackId,
                causedByFailedTaskId);

        internal static TaskRun MakeContinuationTask(
            IEnumerable<TaskRun> existing,
            TaskDefinition input,
            int stageAttempt,
            int recoveryRemaining,
            IEnumerable<TaskRun> occupiedTaskRuns,
            string? causedByFeedbackId = null,
            string? causedByFailedTaskId = null)
        {
            ValidateContinuation(input, recoveryRemaining);

            return MakeTask(
                existing,
                input,
                stageAttempt,
                recoveryRemaining,
                occupiedTaskRuns,
                causedByFeedbackId,
                causedByFailedTaskId);
        }

        internal static void ValidateContinuation(TaskDefinition input, int recoveryRemaining)
        {
            if (input.Recovery is null)
                throw new InvalidOperationException("A continuation task requires a recovery declaration");
        }

        private static TaskRun MakeTask(
            IEnumerable<TaskRun> existing,
            TaskDefinition input,
            int stageAttempt,
            int? recoveryRemaining,
            IEnumerable<TaskRun> occupiedTaskRuns,
            string? causedByFeedbackId,
            string? causedByFailedTaskId)
        {
            var attempt = existing
                              .Where(t => t.DefinitionId == input.Id)
                              .Select(t => t.Attempt)
                              .DefaultIfEmpty(0)
                              .Max() + 1;
            var requiredFiles = ExtractRequiredFiles(input.Expect);
            var classification = DeriveClassification(input.Uses, requiredFiles);
            return new TaskRun
            {
                Id = TaskRunId(input.Id, stageAttempt, attempt, occupiedTaskRuns),
                DefinitionId = input.Id,
                Attempt = attempt,
                Title = input.Title ?? input.Id,
                Uses = input.Uses,
                WithInput = input.With,
                ExpectInput = input.Expect,
                Status = TaskRunStatus.Pending,
                RequiredFiles = requiredFiles.Count > 0 ? requiredFiles : null,
                Artifacts = input.Artifacts,
                SetVars = input.SetVars,
                Recovery = input.Recovery,
                RecoveryRemaining = recoveryRemaining,
                Classification = classification,
                CausedByFeedbackId = causedByFeedbackId,
                CausedByFailedTaskId = causedByFailedTaskId
            };
        }

        private static string TaskRunId(
            string definitionId,
            int stageAttempt,
            int taskAttempt,
            IEnumerable<TaskRun> occupiedTaskRuns)
        {
            var candidate = stageAttempt == 1
                ? $"{definitionId}.{taskAttempt}"
                : $"{definitionId}.s{stageAttempt}.{taskAttempt}";
            var occupied = occupiedTaskRuns.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
            if (occupied.Add(candidate)) return candidate;

            for (var runAttempt = 2; ; runAttempt++)
            {
                var disambiguated = $"{candidate}.run{runAttempt}";
                if (occupied.Add(disambiguated)) return disambiguated;
            }
        }
    }

    public static TaskDefinition ToDefinition(this TaskRun task) => new(
        task.DefinitionId,
        task.Title,
        task.Uses ?? string.Empty,
        task.WithInput,
        task.ExpectInput,
        task.Artifacts,
        task.SetVars,
        task.Recovery);
}
