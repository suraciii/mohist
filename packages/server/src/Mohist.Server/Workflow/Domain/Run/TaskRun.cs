using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Contracts;
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
public enum TaskRunStatus { Pending, Running, Completed, Failed, Cancelled, Interrupted }

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
    /// Additive update-interruption visibility for this attempt. The old
    /// attempt keeps <c>interrupted</c> while its replacement advances
    /// independently through recovering and recovered.
    /// </summary>
    public AgentWorkInterruptionTransition? AgentInterruption { get; set; }
    /// <summary>
    /// Recovery generation for a replacement attempt. The original attempt is
    /// generation zero; interrupted history remains immutable while the next
    /// attempt receives the incremented generation.
    /// </summary>
    public int RecoveryGeneration { get; set; }

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

    /// <summary>
    /// Additive verification metadata populated only for tasks whose
    /// <see cref="DefinitionId"/> matches a built-in lane id from
    /// <c>VerificationLaneCatalog</c>. The presence of this value
    /// identifies a recognized lane attempt and stores its identity, order,
    /// configured budget, terminal outcome, and the underlying attempt/task
    /// identity so a later <c>TaskReport</c> classification can be
    /// persisted in the same state transition as the normal task result.
    /// Recovery helpers (<c>recover:fix-ci</c>) and arbitrary user tasks
    /// leave this null and never participate in the lane gate.
    /// </summary>
    public VerificationLaneAttempt? Lane { get; set; }
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

        internal static TaskRun MakeRecoveryAttempt(
            TaskRun interrupted,
            IEnumerable<TaskRun> existing,
            int stageAttempt,
            int recoveryGeneration,
            string workId,
            string agentTurnId,
            IEnumerable<TaskRun> occupiedTaskRuns,
            DateTimeOffset now)
        {
            if (interrupted.AgentResultSettlement is not { } originalSettlement)
                throw new InvalidOperationException("A recovery attempt requires an Agent result settlement");
            if (recoveryGeneration <= originalSettlement.RecoveryGeneration)
                throw new InvalidOperationException("A recovery attempt must advance the recovery generation");
            if (string.IsNullOrWhiteSpace(workId) || string.IsNullOrWhiteSpace(agentTurnId))
                throw new ArgumentException("A recovery attempt requires new work and turn identities");

            var task = MakeTask(
                existing,
                interrupted.ToDefinition(),
                stageAttempt,
                recoveryRemaining: interrupted.RecoveryRemaining,
                occupiedTaskRuns,
                interrupted.CausedByFeedbackId,
                interrupted.CausedByFailedTaskId);
            task.WorkId = workId;
            task.RecoveryGeneration = recoveryGeneration;
            task.AgentResultSettlement = new AgentResultSettlement
            {
                State = AgentResultSettlementState.AwaitingResult,
                TaskRunId = task.Id,
                WorkId = workId,
                RunnerId = originalSettlement.RunnerId,
                RecoveryGeneration = recoveryGeneration,
                UpdateOperationId = originalSettlement.UpdateOperationId,
                // The session will confirm the physical turn when the
                // replacement dispatch is accepted. Keep a durable logical
                // turn allocation now so the replacement is distinct even
                // before the Runner binds it.
                AgentTurnId = agentTurnId,
                Runtime = originalSettlement.Runtime
            };
            task.AgentInterruption = (interrupted.AgentInterruption ?? new AgentWorkInterruptionTransition(
                AgentWorkInterruptionStates.Interrupted,
                originalSettlement.UpdateOperationId ?? string.Empty,
                originalSettlement.WorkId,
                originalSettlement.TaskRunId,
                originalSettlement.RecoveryGeneration,
                originalSettlement.AgentTurnId,
                null,
                null,
                "The replacement Runner dispatch will resume this work.",
                now)) with
            {
                State = AgentWorkInterruptionStates.Recovering,
                WorkId = workId,
                TaskRunId = task.Id,
                RecoveryGeneration = recoveryGeneration,
                ReplacementTurnId = agentTurnId,
                RecordedAt = now,
            };
            return task;
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
            var id = TaskRunId(input.Id, stageAttempt, attempt, occupiedTaskRuns);
            var task = new TaskRun
            {
                Id = id,
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

            // Recognize the task at creation time so a lane attempt exists in
            // the same state transition that materializes the task. A pending
            // lane is then visible to the status projection and the gate
            // before any report arrives, and a retry for the same lane gets a
            // new attempt identity with the same lane id, order, and budget.
            if (VerificationLaneCatalog.IsKnownLane(input.Id))
            {
                task.Lane = new VerificationLaneAttempt(
                    LaneId: input.Id,
                    Order: VerificationLaneCatalog.OrderOf(input.Id),
                    ConfiguredBudgetMs: TryGetConfiguredBudgetMs(input.With),
                    Outcome: VerificationLaneOutcome.Pending,
                    TaskRunId: id);
            }

            return task;
        }

        /// <summary>
        /// Reads the lane's configured execution budget from the task's
        /// <c>with.timeout</c> value, in milliseconds. A literal positive
        /// finite number is the only valid budget; anything else (missing,
        /// non-numeric, zero, or negative) is treated as <c>0</c> so the lane
        /// projection never fabricates a budget the profile did not declare.
        /// </summary>
        internal static int TryGetConfiguredBudgetMs(Dictionary<string, JsonElement?>? with)
        {
            if (with is null
                || !with.TryGetValue("timeout", out var timeout)
                || !timeout.HasValue)
            {
                return 0;
            }

            var parsed = timeout.Value.ValueKind switch
            {
                JsonValueKind.Number when timeout.Value.TryGetInt32(out var ms) => ms,
                JsonValueKind.String when int.TryParse(timeout.Value.GetString(), out var ms) => ms,
                _ => 0,
            };
            return parsed > 0 ? parsed : 0;
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
