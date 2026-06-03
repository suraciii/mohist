using Mohist.Server.Issue.Queries;

namespace Mohist.Server.Api;

public static class IssueRepositoryResolutionHelpers
{
    public const string DefaultRepoPath = ".";
    public const string DefaultBaseBranch = "main";

    public static IResult? CheckRepositoryConfigured(IssueReadModel? issue)
    {
        if (issue is null) return null;

        if (issue.Repository is not null) return null;

        if (issue.RepositoryProblem is not null)
        {
            var problem = issue.RepositoryProblem;
            return ApiResults.Conflict(
                problem.Message,
                RepositoryProblemCodeToApiCode(problem.Code),
                problem);
        }

        return ApiResults.Conflict(
            "Issue has no resolved repository context",
            "repository_unresolved");
    }

    public static IResult? CheckRepositoryConfigured(IssueInfo? issue)
    {
        if (issue is null) return null;

        if (issue.Repository is not null) return null;

        if (issue.RepositoryProblem is not null)
        {
            var problem = issue.RepositoryProblem;
            return ApiResults.Conflict(
                problem.Message,
                RepositoryProblemCodeToApiCode(problem.Code),
                problem);
        }

        return ApiResults.Conflict(
            "Issue has no resolved repository context",
            "repository_unresolved");
    }

    public static string RepositoryProblemCodeToApiCode(IssueRepositoryProblemCode code) => code switch
    {
        IssueRepositoryProblemCode.ProjectMissing => "repository_project_missing",
        IssueRepositoryProblemCode.ProjectHasNoRepositories => "repository_project_has_no_repositories",
        IssueRepositoryProblemCode.DefaultRepositoryMissing => "repository_default_missing",
        IssueRepositoryProblemCode.RepositoryNotFound => "repository_not_found",
        IssueRepositoryProblemCode.AmbiguousReference => "repository_ambiguous_reference",
        _ => "repository_problem",
    };
}
