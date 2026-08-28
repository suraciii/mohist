using System.Text.Json;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public static partial class WorkflowRunStateDataUpgrader
{
    private enum LegacyTaskInterruptionChange
    {
        RemoveInterruption,
        FailInterruptedAttempt,
    }

    private sealed record LegacyInterruptionPlan(
        Dictionary<(int StageIndex, int TaskIndex), LegacyTaskInterruptionChange> TaskChanges,
        HashSet<int> StageChanges,
        IReadOnlyList<string> Classifications)
    {
        public bool HasChanges => TaskChanges.Count > 0 || StageChanges.Count > 0;
    }

    private static LegacyInterruptionPlan BuildLegacyInterruptionPlan(JsonElement root)
    {
        var taskChanges = new Dictionary<(int StageIndex, int TaskIndex), LegacyTaskInterruptionChange>();
        var stageChanges = new HashSet<int>();
        var classifications = new List<string>();
        if (!TryGetProperty(root, "stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
            return new LegacyInterruptionPlan(taskChanges, stageChanges, classifications);

        string? ownerId = null;
        var stageIndex = 0;
        foreach (var stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object)
            {
                stageIndex++;
                continue;
            }

            if (TryGetProperty(stage, "interruption", out var stageInterruption))
            {
                if (stageInterruption.ValueKind == JsonValueKind.Null)
                {
                    stageChanges.Add(stageIndex);
                    classifications.Add("stale-terminal-interruption");
                }
                else
                {
                    ownerId ??= RequiredString(root, "id", "WorkflowRun id");
                    ValidateInterruption(stageInterruption, ownerId, $"stage index {stageIndex}");
                    if (!HasTerminalChecksProof(stage, stageInterruption))
                    {
                        throw new InvalidOperationException(
                            $"Ambiguous checks interruption in stage index {stageIndex}: exact terminal checks facts are absent");
                    }

                    stageChanges.Add(stageIndex);
                    classifications.Add("provably-terminal-checks-interruption");
                }
            }

            if (!TryGetProperty(stage, "tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            {
                stageIndex++;
                continue;
            }

            var taskIndex = 0;
            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind != JsonValueKind.Object)
                {
                    taskIndex++;
                    continue;
                }

                var interruptedStatus = HasLegacyInterruptedStatus(task);
                var hasInterruption = TryGetProperty(task, "interruption", out var taskInterruption);
                if (!interruptedStatus && !hasInterruption)
                {
                    taskIndex++;
                    continue;
                }

                var taskId = RequiredString(task, "id", $"task id in stage index {stageIndex}");
                if (hasInterruption && taskInterruption.ValueKind != JsonValueKind.Null)
                {
                    ownerId ??= RequiredString(root, "id", "WorkflowRun id");
                    ValidateInterruption(taskInterruption, ownerId, $"task '{taskId}'");
                }

                if (interruptedStatus)
                {
                    if (hasInterruption
                        && taskInterruption.ValueKind != JsonValueKind.Null
                        && !InterruptionMatchesTask(task, taskInterruption))
                    {
                        throw new InvalidOperationException(
                            $"Ambiguous interrupted task '{taskId}' in stage index {stageIndex}: work identity does not match");
                    }
                    if (!HasExactTaskFailureProof(root, stage, task))
                    {
                        throw new InvalidOperationException(
                            $"Ambiguous interrupted task '{taskId}' in stage index {stageIndex}: exact task failure facts are absent");
                    }

                    taskChanges[(stageIndex, taskIndex)] = LegacyTaskInterruptionChange.FailInterruptedAttempt;
                    classifications.Add("provably-failed-action-attempt");
                    if (hasInterruption)
                        classifications.Add("stale-terminal-interruption");
                    taskIndex++;
                    continue;
                }

                if (taskInterruption.ValueKind == JsonValueKind.Null)
                {
                    taskChanges[(stageIndex, taskIndex)] = LegacyTaskInterruptionChange.RemoveInterruption;
                    classifications.Add("stale-terminal-interruption");
                    taskIndex++;
                    continue;
                }

                if (!HasCanonicalTerminalTaskProof(task)
                    || !InterruptionMatchesTask(task, taskInterruption))
                {
                    throw new InvalidOperationException(
                        $"Ambiguous task interruption for '{taskId}' in stage index {stageIndex}: exact terminal task facts are absent");
                }

                taskChanges[(stageIndex, taskIndex)] = LegacyTaskInterruptionChange.RemoveInterruption;
                classifications.Add("stale-terminal-interruption");
                taskIndex++;
            }

            stageIndex++;
        }

        return new LegacyInterruptionPlan(taskChanges, stageChanges, classifications);
    }

    private static bool HasLegacyInterruptedStatus(JsonElement task)
    {
        if (!TryGetProperty(task, "status", out var status))
            return false;
        return status.ValueKind switch
        {
            JsonValueKind.String => string.Equals(status.GetString(), "interrupted", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => status.TryGetInt32(out var value) && value == 5,
            _ => false,
        };
    }

    private static bool HasCanonicalTerminalTaskProof(JsonElement task)
    {
        if (!TryGetProperty(task, "status", out var status)
            || status.ValueKind != JsonValueKind.String
            || status.GetString() is not { } value
            || !(value.Equals("completed", StringComparison.OrdinalIgnoreCase)
                || value.Equals("failed", StringComparison.OrdinalIgnoreCase)
                || value.Equals("cancelled", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return TryGetProperty(task, "finishedAt", out var finishedAt)
            && finishedAt.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(finishedAt.GetString(), out _)
            && TryReadNonEmptyString(task, "workId", out _);
    }

    private static bool HasExactTaskFailureProof(JsonElement root, JsonElement stage, JsonElement task)
    {
        if (!StatusEquals(root, "failed")
            || !StatusEquals(stage, "failed")
            || !TryReadNonEmptyString(stage, "id", out var stageId)
            || !TryReadNonEmptyString(task, "id", out var taskId)
            || !TryReadNonEmptyString(task, "workId", out _)
            || !TryReadNonEmptyString(root, "currentStageId", out var currentStageId)
            || !string.Equals(stageId, currentStageId, StringComparison.Ordinal)
            || !TryGetProperty(task, "finishedAt", out var finishedAt)
            || finishedAt.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(finishedAt.GetString(), out _))
        {
            return false;
        }

        return TryGetProperty(root, "failure", out var runFailure)
            && FailureMatchesTask(runFailure, stageId, taskId)
            && TryGetProperty(stage, "failure", out var stageFailure)
            && FailureMatchesTask(stageFailure, stageId, taskId);
    }

    private static bool FailureMatchesTask(JsonElement failure, string stageId, string taskId) =>
        failure.ValueKind == JsonValueKind.Object
        && TryReadNonEmptyString(failure, "reason", out var reason)
        && string.Equals(reason, "taskFailed", StringComparison.OrdinalIgnoreCase)
        && TryReadNonEmptyString(failure, "stage", out var failureStage)
        && string.Equals(failureStage, stageId, StringComparison.Ordinal)
        && TryReadNonEmptyString(failure, "taskId", out var failureTask)
        && string.Equals(failureTask, taskId, StringComparison.Ordinal);

    private static bool HasTerminalChecksProof(JsonElement stage, JsonElement interruption)
    {
        if (!TryReadNonEmptyString(interruption, "workId", out var interruptedWorkId)
            || !TryReadNonEmptyString(stage, "terminalChecksWorkId", out var terminalWorkId)
            || !string.Equals(interruptedWorkId, terminalWorkId, StringComparison.Ordinal)
            || !TryReadNonEmptyString(stage, "terminalChecksWorkerId", out _)
            || !TryReadNonEmptyString(stage, "terminalChecksResultFingerprint", out _)
            || (TryGetProperty(stage, "checksWorkId", out var currentWorkId)
                && currentWorkId.ValueKind is not JsonValueKind.Null))
        {
            return false;
        }

        if (!TryGetProperty(stage, "checks", out var checks)
            || checks.ValueKind != JsonValueKind.Array
            || checks.GetArrayLength() == 0)
        {
            return false;
        }

        foreach (var check in checks.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object
                || !TryGetProperty(check, "status", out var status)
                || status.ValueKind != JsonValueKind.String
                || status.GetString() is not { } value
                || !(value.Equals("passed", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("failed", StringComparison.OrdinalIgnoreCase))
                || !TryGetProperty(check, "finishedAt", out var finishedAt)
                || finishedAt.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(finishedAt.GetString(), out _))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateInterruption(JsonElement interruption, string ownerId, string location)
    {
        if (interruption.ValueKind != JsonValueKind.Object
            || !TryReadNonEmptyString(interruption, "reasonCode", out _)
            || !TryReadNonEmptyString(interruption, "workId", out _)
            || !TryReadNonEmptyString(interruption, "ownerId", out var interruptionOwner)
            || !string.Equals(interruptionOwner, ownerId, StringComparison.Ordinal)
            || !TryReadDateTimeOffset(interruption, "recordedAt")
            || !TryReadDateTimeOffset(interruption, "recoveryDeadlineAt"))
        {
            throw new InvalidOperationException($"Malformed legacy interruption at {location}");
        }
    }

    private static bool InterruptionMatchesTask(JsonElement task, JsonElement interruption) =>
        TryReadNonEmptyString(task, "workId", out var taskWorkId)
        && TryReadNonEmptyString(interruption, "workId", out var interruptionWorkId)
        && string.Equals(taskWorkId, interruptionWorkId, StringComparison.Ordinal);

    private static bool StatusEquals(JsonElement value, string expected) =>
        TryReadNonEmptyString(value, "status", out var status)
        && string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);

    private static string RequiredString(JsonElement value, string name, string description)
    {
        if (!TryReadNonEmptyString(value, name, out var result))
            throw new InvalidOperationException($"Cannot migrate legacy interruption: {description} is missing");
        return result;
    }

    private static bool TryReadNonEmptyString(JsonElement value, string name, out string result)
    {
        if (TryGetProperty(value, name, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            result = property.GetString()!;
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static bool TryReadDateTimeOffset(JsonElement value, string name) =>
        TryGetProperty(value, name, out var property)
        && property.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(property.GetString(), out _);

    private static bool ContainsLegacyInterruptionShape(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetProperty(root, "stages", out var stages)
                || stages.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var stage in stages.EnumerateArray())
            {
                if (stage.ValueKind != JsonValueKind.Object)
                    continue;
                if (TryGetProperty(stage, "interruption", out _))
                    return true;
                if (!TryGetProperty(stage, "tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var task in tasks.EnumerateArray())
                {
                    if (task.ValueKind == JsonValueKind.Object
                        && (TryGetProperty(task, "interruption", out _) || HasLegacyInterruptedStatus(task)))
                    {
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}
