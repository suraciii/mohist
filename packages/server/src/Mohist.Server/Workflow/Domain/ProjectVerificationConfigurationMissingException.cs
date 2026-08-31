using Orleans;

namespace Mohist.Server.Workflow.Domain;

/// <summary>Start-time rejection for a Project without its required verification command.</summary>
[GenerateSerializer]
public sealed class ProjectVerificationConfigurationMissingException : InvalidOperationException
{
    [Id(0)]
    public string ProjectId { get; }

    public ProjectVerificationConfigurationMissingException(string projectId)
        : base(
            $"Project '{projectId}' has no verification command. Configure it with `mo project workflow verification set` or in Project Settings > Workflows.")
    {
        ProjectId = projectId;
    }
}
