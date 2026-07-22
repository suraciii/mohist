using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Grains;

internal static class RuntimeTaskFollowUps
{
    internal static IReadOnlyList<(TaskDefinition Definition, int? RecoveryRemaining)> Project(
        IReadOnlyList<RuntimeTaskInput>? tasks)
    {
        if (tasks is not { Count: > 0 }) return [];

        return tasks.Select(task =>
        {
            var definition = new TaskDefinition(
                task.Id,
                task.Title,
                task.Uses ?? string.Empty,
                WorkflowDispatchHelpers.ParseWith(task.With),
                WorkflowDispatchHelpers.ParseWith(task.Expect),
                task.Artifacts,
                task.SetVars,
                task.Recovery);
            if (task.Recovery is not null)
            {
                if (task.RecoveryRemaining is null)
                    throw new InvalidOperationException(
                        $"Recovery follow-up task '{task.Id}' must carry an explicit numeric recoveryRemaining");
                TaskRun.ValidateContinuation(definition, task.RecoveryRemaining.Value);
            }
            else if (task.RecoveryRemaining is not null)
            {
                throw new InvalidOperationException(
                    $"Task follow-up '{task.Id}' carries recoveryRemaining without a recovery declaration");
            }

            return (Definition: definition, RecoveryRemaining: task.RecoveryRemaining);
        }).ToList();
    }
}
