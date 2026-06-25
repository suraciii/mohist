using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;

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
public enum TaskRunStatus { Pending, Running, Completed, Failed }

public sealed class TaskRun
{
    public required string Id { get; init; }
    public required string DefinitionId { get; init; }
    public required int Attempt { get; init; }
    public required string Title { get; init; }
    public string? Uses { get; init; }
    public Dictionary<string, JsonElement?>? WithInput { get; init; }
    public TaskRunStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? RunnerId { get; set; }
    public string? WorkId { get; set; }
    public IReadOnlyList<WorkflowTaskRequiredFile>? RequiredFiles { get; init; }
    public TaskArtifactCapture? Artifacts { get; init; }
    public Dictionary<string, string>? SetVars { get; init; }
    public TaskFailureAction? OnFailure { get; init; }
    public TaskClassification Classification { get; init; } = TaskClassification.UserFacing;
    public string? CausedByFeedbackId { get; init; }
    public string? CausedByFailedTaskId { get; init; }
    public JsonElement? Output { get; set; }
}

public static class TaskRunExtensions
{
    private const string ExpectKey = "expect";
    private const string FilesKey = "files";
    private const string MarkersKey = "markers";
    private const string SessionKey = "session";

    public static string? ExtractSessionName(Dictionary<string, JsonElement?>? withInput)
    {
        if (withInput is null) return null;
        if (!withInput.TryGetValue(SessionKey, out var session) || !session.HasValue)
            return null;
        return session.Value.ValueKind == JsonValueKind.String
            ? session.Value.GetString()
            : null;
    }

    public static IReadOnlyList<WorkflowTaskRequiredFile> ExtractRequiredFiles(Dictionary<string, JsonElement?>? withInput)
    {
        if (withInput is null) return [];

        if (!withInput.TryGetValue(ExpectKey, out var expect) || !expect.HasValue || expect.Value.ValueKind != JsonValueKind.Object)
            return [];

        var result = new List<WorkflowTaskRequiredFile>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        if (expect.Value.TryGetProperty(MarkersKey, out var markerEntries) && markerEntries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in markerEntries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                var path = entry.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                if (string.IsNullOrEmpty(path)) continue;

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

        if (expect.Value.TryGetProperty(FilesKey, out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in files.EnumerateArray())
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
        if (uses is not null && (uses.StartsWith("core/") || uses.StartsWith("mohist/")) && !uses.Contains("acp-agent"))
            return TaskClassification.Orchestration;
        return TaskClassification.UserFacing;
    }

    extension(TaskRun)
    {
        internal static TaskRun MakeTask(
            IEnumerable<TaskRun> existing,
            TaskDefinition input,
            string? causedByFeedbackId = null,
            string? causedByFailedTaskId = null)
        {
            var attempt = existing
                              .Where(t => t.DefinitionId == input.Id)
                              .Select(t => t.Attempt)
                              .DefaultIfEmpty(0)
                              .Max() + 1;
            var requiredFiles = ExtractRequiredFiles(input.With);
            var classification = DeriveClassification(input.Uses, requiredFiles);
            return new TaskRun
            {
                Id = $"{input.Id}.{attempt}",
                DefinitionId = input.Id,
                Attempt = attempt,
                Title = input.Title,
                Uses = input.Uses,
                WithInput = input.With,
                Status = TaskRunStatus.Pending,
                RequiredFiles = requiredFiles.Count > 0 ? requiredFiles : null,
                Artifacts = input.Artifacts,
                SetVars = input.SetVars,
                OnFailure = input.OnFailure,
                Classification = classification,
                CausedByFeedbackId = causedByFeedbackId,
                CausedByFailedTaskId = causedByFailedTaskId
            };
        }
    }
}
