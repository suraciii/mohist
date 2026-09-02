using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// Builds the flat runner payload owned by an AgentJob dispatch.
/// </summary>
internal static class AgentJobDispatchProjector
{
    public static Dictionary<string, JsonElement> BuildWith(
        AgentJobInput input,
        string? executionSource)
    {
        var with = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["prompt"] = JSON.SerializeToElement(
                AgentStartupContextComposer.ComposePrompt(input.Prompt, input.StartupContext)),
        };
        if (executionSource is not null)
            with["executionSource"] = JSON.SerializeToElement(executionSource);
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
        else if (executionSource is null)
        {
            if (input.SlackExecutionContext is not null)
                throw new InvalidOperationException("Legacy AgentJob Slack context could not be reconciled.");
        }
        else if (string.Equals(executionSource, AgentExecutionSources.NonSlack, StringComparison.Ordinal))
        {
            if (input.SlackExecutionContext is not null)
                throw new InvalidOperationException("Non-Slack AgentJob input cannot carry a Slack execution context.");
        }
        else
        {
            throw new InvalidOperationException($"Unknown AgentJob execution source '{executionSource}'.");
        }

        return with;
    }
}
