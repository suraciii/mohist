using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Domain.Run;

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
    public List<TaskOutputDefinition>? Outputs { get; init; }
    public TaskClassification Classification { get; init; } = TaskClassification.UserFacing;
    public string? CausedByFeedbackId { get; init; }
}

public static class TaskRunExtensions
{
    private const string ExpectKey = "expect";
    private const string FilesKey = "files";
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

        if (!expect.Value.TryGetProperty(FilesKey, out var files) || files.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<WorkflowTaskRequiredFile>();
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

            result.Add(new WorkflowTaskRequiredFile(path, "task-expect", CanFetchContent: true, markers));
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
        internal static TaskRun MakeTask(IEnumerable<TaskRun> existing, TaskDefinition input, string? causedByFeedbackId = null)
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
                Outputs = input.Outputs,
                Classification = classification,
                CausedByFeedbackId = causedByFeedbackId
            };
        }
    }
}
