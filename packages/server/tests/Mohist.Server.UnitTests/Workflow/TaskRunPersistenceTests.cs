using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow;

public sealed class TaskRunPersistenceTests
{
    [Fact]
    public void Deserialize_OldTaskRunWithoutTerminalReplayFields_LeavesThemUnset()
    {
        const string oldState = """
        {
          "Id": "task-1.1",
          "DefinitionId": "task-1",
          "Attempt": 1,
          "Title": "Task 1",
          "Status": 0,
          "StartedAt": "2026-08-24T00:00:00+00:00",
          "WorkerId": "runner-1",
          "WorkId": "work-1"
        }
        """;

        var task = JsonSerializer.Deserialize<TaskRun>(oldState, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.NotNull(task);
        Assert.Null(task.TerminalResultFingerprint);
        Assert.Null(task.TerminalExecutionBinding);
        Assert.Equal(TaskRunStatus.Pending, task.Status);
    }
}
