using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private async Task EnsureRoutedInitialLaunchAsync(
        RoutedAgentLaunchPlan plan,
        IAgentSessionGrain sessionGrain,
        AgentSessionMetadata metadata)
    {
        if (State.Input is null && !string.IsNullOrWhiteSpace(plan.Prompt))
        {
            var input = new AgentJobInput(
                Prompt: plan.Prompt!,
                Model: plan.Model,
                WorkspacePath: plan.WorkspacePath,
                ProjectId: plan.ProjectId,
                Runtime: plan.Runtime ?? AgentConfigSchema.DefaultRuntime,
                AgentId: plan.AgentId,
                AgentInstructions: plan.AgentInstructions,
                AgentConfig: DeserializeAgentConfig(plan.AgentConfigJson),
                AgentSessionId: plan.SessionId,
                Variant: plan.Variant,
                ReasoningEffort: plan.ReasoningEffort,
                IssueNumber: plan.IssueNumber,
                EpicNumber: plan.EpicNumber,
                WorkflowRunId: plan.WorkflowRunId,
                InitialInputId: $"agent-job-input:{Key}",
                InitialTurnId: $"agent-job-turn:{Key}",
                Skills: plan.Skills,
                ExecutionSource: AgentExecutionSources.NonSlack);
            State.AgentConfigJson = plan.AgentConfigJson;
            State.Input = input with { AgentConfig = null };
            State.SubmittedAt = _timeProvider.GetUtcNow();
        }

        if (State.Input is null)
            return;

        if (string.IsNullOrWhiteSpace(State.Input.InitialInputId)
            || string.IsNullOrWhiteSpace(State.Input.InitialTurnId))
        {
            State.Input = State.Input with
            {
                InitialInputId = State.Input.InitialInputId ?? $"agent-job-input:{Key}",
                InitialTurnId = State.Input.InitialTurnId ?? $"agent-job-turn:{Key}",
            };
        }

        if (string.IsNullOrWhiteSpace(State.Input.Prompt))
            return;

        // The capacity claim reads the persisted Session owner in its own
        // transaction, so the Job-owned launch identity must be durable
        // before admission can evaluate local order.
        await sessionGrain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: State.Input.InitialInputId!,
            TurnId: State.Input.InitialTurnId!,
            Prompt: State.Input.Prompt,
            Source: "agent-launch",
            JobId: Key,
            Metadata: metadata,
            Runtime: plan.Runtime ?? AgentConfigSchema.DefaultRuntime,
            WorkDir: plan.WorkspacePath,
            Definition: new AgentExecutionDefinition(
                plan.AgentInstructions ?? string.Empty,
                plan.Runtime ?? AgentConfigSchema.DefaultRuntime,
                plan.Model,
                plan.Variant,
                plan.Skills ?? [],
                ReasoningEffort: plan.ReasoningEffort)));
    }
}
