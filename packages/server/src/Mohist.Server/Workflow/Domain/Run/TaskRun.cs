using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;

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
}

internal static class TaskRunExtensions
{
    extension(TaskRun)
    {
        internal static TaskRun MakeTask(IEnumerable<TaskRun> existing, TaskDefinition input)
        {
            var attempt = existing
                              .Where(t => t.DefinitionId == input.Id)
                              .Select(t => t.Attempt)
                              .DefaultIfEmpty(0)
                              .Max() + 1;
            return new TaskRun
            {
                Id = $"{input.Id}.{attempt}",
                DefinitionId = input.Id,
                Attempt = attempt,
                Title = input.Title,
                Uses = input.Uses,
                WithInput = input.With,
                Status = TaskRunStatus.Pending
            };
        }
    }
}
