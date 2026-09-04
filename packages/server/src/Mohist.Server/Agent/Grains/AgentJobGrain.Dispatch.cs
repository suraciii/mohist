using System.Text.Json;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private static AgentJobInput WithRuntimeDefaultForSubmission(AgentJobInput input, AgentJobInput? existingInput) =>
        existingInput is not null && string.IsNullOrWhiteSpace(existingInput.Runtime)
            ? input
            : input with
            {
                Runtime = string.IsNullOrWhiteSpace(input.Runtime)
                    ? AgentConfigSchema.DefaultRuntime
                    : input.Runtime,
            };

    private async Task<WorkDispatch> BuildDispatchAsync(string workId)
    {
        var input = InputWithAgentConfig()!;
        // Null is the durable marker for an input written before the
        // discriminator existed. Only trusted persisted Slack context may
        // reconcile that legacy state to Slack; an explicit non-Slack value
        // must never be rewritten around a context mismatch.
        var executionSource = input.ExecutionSource is null && input.SlackExecutionContext is not null
            ? AgentExecutionSources.Slack
            : input.ExecutionSource;
        var workflowOrigin = input.WorkflowOrigin;
        var payload = !string.IsNullOrWhiteSpace(workflowOrigin?.VariablesJson)
            ? JSON.Deserialize<Dictionary<string, JsonElement?>>(workflowOrigin.VariablesJson!)
                ?? new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(input.WorkspaceName))
            payload["workspace"] = JSON.SerializeToElement(new
            {
                name = input.WorkspaceName,
                repositories = (input.WorkspaceRepositories is { Count: > 0 }
                    ? input.WorkspaceRepositories
                        .Select(r => (object)new { name = r.Name, gitUrl = r.GitUrl })
                        .ToList()
                    : []),
            });
        else if (!string.IsNullOrWhiteSpace(input.WorkspacePath))
            payload["workspace"] = JSON.SerializeToElement(new { path = input.WorkspacePath });

        var variablesJson = payload.Count == 0 ? null : JSON.Serialize(payload);

        var with = AgentJobDispatchProjector.BuildWith(input, executionSource);
        return new WorkDispatch(
            WorkflowRunId: workflowOrigin?.WorkflowRunId ?? string.Empty,
            WorkId: workId,
            Uses: null,
            With: JSON.Serialize(with),
            Variables: variablesJson,
            WorkType: "agent-job",
            Stage: workflowOrigin?.Stage ?? "agent",
            Title: workflowOrigin?.ActionAttemptId ?? "Agent Job",
            Issue: input.ProjectId is not null && input.IssueNumber is > 0
                ? new WorkIssueRef(input.ProjectId, input.IssueNumber.Value)
                : null,
            Artifacts: workflowOrigin?.ArtifactsJson,
            SetVars: workflowOrigin?.SetVarsJson,
            Recovery: workflowOrigin?.RecoveryJson,
            RecoveryRemaining: workflowOrigin?.RecoveryRemaining,
            Expect: workflowOrigin?.ExpectJson,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob,
            AgentJobId: Key,
            ProjectId: string.IsNullOrWhiteSpace(input.ProjectId) ? null : input.ProjectId,
            AgentSessionId: string.IsNullOrWhiteSpace(input.AgentSessionId) ? null : input.AgentSessionId,
            AgentId: input.AgentId,
            InitialInputId: string.IsNullOrWhiteSpace(input.InitialInputId) ? null : input.InitialInputId,
            InitialTurnId: string.IsNullOrWhiteSpace(input.InitialTurnId) ? null : input.InitialTurnId,
            AgentDefinition: ExecutionDefinitionFrom(input),
            PinnedRunnerId: input.PinnedRunnerId,
            AgentSessionStartup: input.AgentSessionStartup,
            ActionAttemptId: workflowOrigin?.ActionAttemptId,
            OriginMarker: input.OriginMarker);
    }
}
