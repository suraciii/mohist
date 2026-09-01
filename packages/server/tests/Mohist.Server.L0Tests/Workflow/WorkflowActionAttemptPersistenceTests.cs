using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow;

public sealed class WorkflowActionAttemptPersistenceTests
{
    [Fact]
    public void Deserialize_OldWorkflowActionAttemptWithoutTerminalFingerprint_LeavesItUnset()
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

        var task = JsonSerializer.Deserialize<WorkflowActionAttempt>(oldState, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.NotNull(task);
        Assert.Null(task.TerminalResultFingerprint);
        Assert.Equal(WorkflowActionAttemptStatus.Pending, task.Status);
    }
}
