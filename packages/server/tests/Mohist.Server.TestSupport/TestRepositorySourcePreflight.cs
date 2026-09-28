using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;

namespace Mohist.Server.TestSupport;

public sealed class TestRepositorySourcePreflight : IRepositorySourcePreflight
{
    public Task<RepositorySourcePreflightResult> CheckAsync(
        string projectId,
        RepositoryInfo repository,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(repository.ResolvedBaseBranch, "preflight-invalid", StringComparison.Ordinal))
        {
            return Task.FromResult(RepositorySourcePreflightResult.Invalid(
                "repository_base_branch_missing",
                "repository.baseBranch",
                $"Repository '{repository.Name}' does not have remote base branch 'preflight-invalid'.",
                "Select an existing remote base branch and retry. No Workspace, WorkflowRun, or AgentJob was created."));
        }

        return Task.FromResult(RepositorySourcePreflightResult.Valid());
    }
}
