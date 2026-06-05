namespace Mohist.Server.Project.Services;

public sealed class ProjectRefResolver
{
    private readonly ProjectQuerier _projects;

    public ProjectRefResolver(ProjectQuerier projects)
    {
        _projects = projects;
    }

    public Task<ProjectInfo?> ResolveAsync(string? projectRef)
    {
        return string.IsNullOrWhiteSpace(projectRef)
            ? Task.FromResult<ProjectInfo?>(null)
            : _projects.ResolveByIdOrNameAsync(projectRef);
    }
}
