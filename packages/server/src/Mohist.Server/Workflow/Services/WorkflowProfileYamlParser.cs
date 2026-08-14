using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

internal static class WorkflowProfileYamlParser
{
    public static WorkflowProfile Parse(
        string yaml,
        string fallbackId,
        ActionCatalog? catalog = null,
        string? agentActionOverride = null)
    {
        var result = WorkflowProfileParser.Parse(yaml, fallbackId, agentActionOverride);
        var errors = result.Errors.ToList();
        if (result.Profile is not null && catalog is not null)
        {
            errors.AddRange(ActionContractValidator.Validate(result.Profile.Definition, catalog));
            if (result.Profile.AgentAction is not null)
            {
                errors.AddRange(ActionContractValidator.ValidateAgentAction(
                    result.Profile.Definition,
                    result.Profile.AgentAction,
                    catalog));
            }
        }
        if (errors.Count > 0)
            throw new WorkflowDefinitionValidationException(errors
                .OrderBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Message, StringComparer.Ordinal)
                .ToArray());

        return result.Profile!;
    }
}
