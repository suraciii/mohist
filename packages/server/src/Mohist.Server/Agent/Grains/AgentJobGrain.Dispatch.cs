using System.Text.Json;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private async Task<WorkDispatch> BuildDispatchAsync(string workId)
    {
        var input = InputWithAgentConfig()!;
        // A pre-discriminator Slack job can be reconciled only from its
        // durable Server-created context. It remains a legacy Slack
        // dispatch; it is never relabeled as ordinary non-Slack work.
        var executionSource = input.SlackExecutionContext is not null
            && string.Equals(input.ExecutionSource, AgentExecutionSources.NonSlack, StringComparison.Ordinal)
            ? AgentExecutionSources.Slack
            : input.ExecutionSource;
        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
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

        var with = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["prompt"] = JSON.SerializeToElement(
                AgentStartupContextComposer.ComposePrompt(input.Prompt, input.StartupContext)),
            ["executionSource"] = JSON.SerializeToElement(executionSource),
        };
        if (!string.IsNullOrWhiteSpace(input.AgentInstructions))
            with["instructions"] = JSON.SerializeToElement(input.AgentInstructions);
        if (!string.IsNullOrWhiteSpace(input.Model))
            with["model"] = JSON.SerializeToElement(input.Model);
        if (!string.IsNullOrWhiteSpace(input.Variant))
            with["variant"] = JSON.SerializeToElement(input.Variant);
        if (!string.IsNullOrWhiteSpace(input.ReasoningEffort))
            with["reasoningEffort"] = JSON.SerializeToElement(input.ReasoningEffort);
        if (!string.IsNullOrWhiteSpace(input.Runtime))
            with["runtime"] = JSON.SerializeToElement(input.Runtime);
        if (input.Skills is { Count: > 0 })
            with["skills"] = JSON.SerializeToElement(input.Skills);
        // Carry accepted attachment descriptors without embedding file bytes.
        if (input.Attachments is { Count: > 0 })
            with["attachments"] = JSON.SerializeToElement(input.Attachments
                .Select(descriptor => new
                {
                    id = descriptor.Id,
                    name = descriptor.OriginalFileName,
                    contentType = descriptor.ContentType,
                    size = descriptor.Size,
                })
                .ToArray());
        if (string.Equals(executionSource, AgentExecutionSources.Slack, StringComparison.Ordinal))
        {
            if (input.SlackExecutionContext is null)
                throw new InvalidOperationException("Slack AgentJob input requires a complete execution context.");
            with["slackExecutionContext"] = JSON.SerializeToElement(input.SlackExecutionContext);
        }
        else if (!string.Equals(executionSource, AgentExecutionSources.NonSlack, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown AgentJob execution source '{executionSource}'.");
        }
        else if (input.SlackExecutionContext is not null)
        {
            throw new InvalidOperationException("Non-Slack AgentJob input cannot carry a Slack execution context.");
        }

        return new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            Uses: null,
            With: JSON.Serialize(with),
            Variables: variablesJson,
            WorkType: "agent-job",
            Stage: "agent",
            Title: "Agent Job",
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
            RecoveryGeneration: State.RecoveryGeneration);
    }
}
