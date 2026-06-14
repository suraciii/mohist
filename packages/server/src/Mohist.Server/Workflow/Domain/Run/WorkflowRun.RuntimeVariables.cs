using System.Text.Json;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void CaptureTaskOutputs(string taskDefinitionId, IReadOnlyDictionary<string, JsonElement>? outputs)
        {
            if (outputs is null || outputs.Count == 0)
                return;

            foreach (var (name, value) in outputs)
            {
                run.RuntimeVariables[$"tasks.{taskDefinitionId}.outputs.{name}"] = value.Clone();
            }
        }
    }
}
