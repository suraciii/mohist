using System.Text.Json.Serialization;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Querying;

namespace Mohist.Server.Issue.Querying;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IssueRepositoryProblemCode
{
    None = 0,
    ProjectMissing,
    ProjectHasNoRepositories,
    DefaultRepositoryMissing,
    RepositoryNotFound,
    AmbiguousReference,
}

[GenerateSerializer]
public sealed record IssueRepositoryProblem(
    [property: Id(0)] IssueRepositoryProblemCode Code,
    [property: Id(1)] string Message,
    [property: Id(2)] string? RepositoryRef = null,
    [property: Id(3)] string[]? CandidateNames = null)
{
    public static IssueRepositoryProblem ProjectMissing(string? projectId) =>
        new(
            IssueRepositoryProblemCode.ProjectMissing,
            string.IsNullOrEmpty(projectId)
                ? "Issue has no project to resolve a repository from"
                : $"Project '{projectId}' could not be loaded",
            RepositoryRef: null,
            CandidateNames: null);

    public static IssueRepositoryProblem ProjectHasNoRepositories(string? projectId) =>
        new(
            IssueRepositoryProblemCode.ProjectHasNoRepositories,
            string.IsNullOrEmpty(projectId)
                ? "Project has no repositories configured"
                : $"Project '{projectId}' has no repositories configured",
            RepositoryRef: null,
            CandidateNames: null);

    public static IssueRepositoryProblem DefaultRepositoryMissing(string? projectId) =>
        new(
            IssueRepositoryProblemCode.DefaultRepositoryMissing,
            string.IsNullOrEmpty(projectId)
                ? "Project has no default repository"
                : $"Project '{projectId}' has no default repository",
            RepositoryRef: null,
            CandidateNames: null);

    public static IssueRepositoryProblem RepositoryNotFound(string repositoryRef, string[]? candidates = null) =>
        new(
            IssueRepositoryProblemCode.RepositoryNotFound,
            $"Repository '{repositoryRef}' was not found in the project configuration",
            RepositoryRef: repositoryRef,
            CandidateNames: candidates);

    public static IssueRepositoryProblem AmbiguousReference(string repositoryRef, string[] candidates) =>
        new(
            IssueRepositoryProblemCode.AmbiguousReference,
            $"Repository reference '{repositoryRef}' matches multiple repositories in the project",
            RepositoryRef: repositoryRef,
            CandidateNames: candidates);
}

[GenerateSerializer]
public sealed record IssueRepositoryResolution(
    [property: Id(0)] RepositoryInfo? Repository,
    [property: Id(1)] IssueRepositoryProblem? Problem)
{
    public bool HasProblem => Problem is not null;

    public string? ResolvedName => Repository?.Name ?? Problem?.RepositoryRef;

    public static IssueRepositoryResolution Resolved(RepositoryInfo repository) =>
        new(repository, null);

    public static IssueRepositoryResolution Missing(IssueRepositoryProblem problem) =>
        new(null, problem);
}

public class IssueRepositoryResolver
{
    public IssueRepositoryResolution Resolve(ProjectInfo? project, string? repositoryRef)
    {
        if (project is null)
            return IssueRepositoryResolution.Missing(IssueRepositoryProblem.ProjectMissing(projectId: null));

        if (project.Repositories.Count == 0)
            return IssueRepositoryResolution.Missing(IssueRepositoryProblem.ProjectHasNoRepositories(project.Id));

        if (string.IsNullOrWhiteSpace(repositoryRef))
        {
            var defaultRepo = project.Repositories.FirstOrDefault(r => r.IsDefault);
            return defaultRepo is not null
                ? IssueRepositoryResolution.Resolved(defaultRepo)
                : IssueRepositoryResolution.Missing(IssueRepositoryProblem.DefaultRepositoryMissing(project.Id));
        }

        var candidates = project.Repositories
            .Where(r => string.Equals(r.Name, repositoryRef, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count > 1)
        {
            return IssueRepositoryResolution.Missing(
                IssueRepositoryProblem.AmbiguousReference(repositoryRef, candidates.Select(c => c.Name).ToArray()));
        }

        if (candidates.Count == 1)
            return IssueRepositoryResolution.Resolved(candidates[0]);

        return IssueRepositoryResolution.Missing(
            IssueRepositoryProblem.RepositoryNotFound(
                repositoryRef,
                project.Repositories.Select(r => r.Name).ToArray()));
    }
}
