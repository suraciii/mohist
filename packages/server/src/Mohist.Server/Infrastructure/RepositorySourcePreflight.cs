using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;

namespace Mohist.Server.Infrastructure;

public sealed class RepositorySourcePreflight : IRepositorySourcePreflight, ISingletonService
{
    private readonly IRunnerControlTransport _control;
    private readonly Func<string, Task<IReadOnlyList<RunnerInfo>>> _eligible;

    public RepositorySourcePreflight(IRunnerControlTransport control, IGrainFactory grains)
        : this(control, projectId => grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).ListEligibleRunnersAsync(projectId)) { }

    internal RepositorySourcePreflight(IRunnerControlTransport control, Func<string, Task<IReadOnlyList<RunnerInfo>>> eligible)
    {
        _control = control;
        _eligible = eligible;
    }

    public async Task<RepositorySourcePreflightResult> CheckAsync(string projectId, RepositoryInfo repository, CancellationToken cancellationToken = default)
    {
        if (!RepositoryPolicy.TryNormalizeGitUrl(repository.GitUrl, out var gitUrl))
            return Invalid(repository, "repository_remote_invalid", "repository.gitUrl", $"Repository '{repository.Name}' has an invalid remote URL.", "Set repository.gitUrl to a reachable Git remote and retry.");
        if (!RepositoryPolicy.TryNormalizeBaseBranch(repository.ResolvedBaseBranch, out var baseBranch))
            return Invalid(repository, "repository_base_branch_invalid", "repository.baseBranch", $"Repository '{repository.Name}' has an invalid base branch.", "Set repository.baseBranch to a non-empty branch name and retry.");
        if (gitUrl.Contains("::", StringComparison.Ordinal)
            || (gitUrl.Contains("://", StringComparison.Ordinal)
                && !gitUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !gitUrl.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
                && !gitUrl.StartsWith("git://", StringComparison.OrdinalIgnoreCase)))
            return Invalid(repository, "repository_remote_unsafe_transport", "repository.gitUrl", $"Repository '{repository.Name}' uses an executable or unsupported Git transport.", "Use HTTPS, SSH, or Git without a custom transport, then retry.");

        IReadOnlyList<RunnerInfo> eligible;
        try { eligible = await _eligible(projectId); }
        catch { return Unavailable(repository); }
        var runner = eligible.FirstOrDefault(candidate => _control.IsConnected(candidate.RunnerId));
        if (runner is null) return Unavailable(repository);
        try
        {
            var result = await _control.SendRequestAsync<RepositoryPreflightParams, RepositoryPreflightResult>(runner.RunnerId, "repository.preflight", new(gitUrl, baseBranch), ct: cancellationToken);
            if (result.ExitCode == 0) return RepositorySourcePreflightResult.Valid();
            if (result.ExitCode == 2)
                return Invalid(repository, "repository_base_branch_missing", "repository.baseBranch", $"Repository '{repository.Name}' does not have remote base branch '{baseBranch}'.", $"Create or select origin/{baseBranch} and retry.");
            if (result.ExitCode is -1 or 124)
                return Invalid(repository, result.ExitCode == -1 ? "repository_git_unavailable" : "repository_remote_timeout", "repository.gitUrl", result.ExitCode == -1 ? "Runner could not run the Git executable." : $"Repository '{repository.Name}' remote check timed out.", "Configure Git/credentials on an eligible Runner, then retry.");
            return Invalid(repository, "repository_remote_unreachable", "repository.gitUrl", $"Repository '{repository.Name}' remote could not be reached.", "Verify the remote URL and credentials, then retry.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Unavailable(repository); }
    }

    private static RepositorySourcePreflightResult Invalid(RepositoryInfo repository, string code, string field, string message, string nextStep) =>
        RepositorySourcePreflightResult.Invalid(code, field, message + " No Workspace, WorkflowRun, or AgentJob was created.", nextStep + " No Workspace, WorkflowRun, or AgentJob was created.");
    private static RepositorySourcePreflightResult Unavailable(RepositoryInfo repository) =>
        Invalid(repository, "repository_runner_unavailable", "repository.gitUrl", $"Repository '{repository.Name}' could not be checked because no connected Runner is available.", "Connect an eligible Runner with repository credentials, then retry.");
}
