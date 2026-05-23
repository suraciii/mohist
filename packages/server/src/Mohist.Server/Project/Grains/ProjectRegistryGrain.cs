using ProjectInfo = Mohist.Server.Project.Domain.ProjectInfo;

namespace Mohist.Server.Project.Grains;

public class ProjectRegistryGrain : Grain, IProjectRegistryGrain
{
    private readonly Dictionary<string, ProjectInfo> _projects = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentProjectName;
    private readonly ILogger<ProjectRegistryGrain> _log;

    public ProjectRegistryGrain(ILogger<ProjectRegistryGrain> log)
    {
        _log = log;
    }

    public Task<ProjectInfo?> GetByNameAsync(string name)
    {
        _projects.TryGetValue(name, out var project);
        return Task.FromResult(project);
    }

    public Task<List<ProjectInfo>> GetAllAsync()
    {
        return Task.FromResult(_projects.Values.ToList());
    }

    public Task<ProjectInfo> CreateAsync(string name, string path, string? baseBranch)
    {
        if (_projects.ContainsKey(name))
            throw new InvalidOperationException($"Project '{name}' already exists");

        var project = new ProjectInfo
        {
            Id = $"proj_{Guid.NewGuid():N}",
            Name = name,
            Path = path,
            BaseBranch = baseBranch ?? "main",
        };

        _projects[name] = project;
        _log.LogInformation("Project {Name} created at {Path}", name, path);
        return Task.FromResult(project);
    }

    public Task<ProjectInfo?> UpdateAsync(string name, string? baseBranch)
    {
        if (!_projects.TryGetValue(name, out var project))
            return Task.FromResult<ProjectInfo?>(null);

        if (baseBranch is not null)
            project.BaseBranch = baseBranch;

        project.UpdatedAt = DateTime.UtcNow.ToString("o");
        return Task.FromResult<ProjectInfo?>(project);
    }

    public Task<bool> DeleteAsync(string name)
    {
        var removed = _projects.Remove(name);
        if (removed && string.Equals(_currentProjectName, name, StringComparison.OrdinalIgnoreCase))
            _currentProjectName = null;
        return Task.FromResult(removed);
    }

    public Task<ProjectInfo?> GetCurrentAsync()
    {
        if (_currentProjectName is null)
            return Task.FromResult<ProjectInfo?>(null);
        _projects.TryGetValue(_currentProjectName, out var project);
        return Task.FromResult(project);
    }

    public Task<ProjectInfo?> SetCurrentAsync(string name)
    {
        if (!_projects.TryGetValue(name, out var project))
            return Task.FromResult<ProjectInfo?>(null);
        _currentProjectName = name;
        _log.LogInformation("Current project set to {Name}", name);
        return Task.FromResult<ProjectInfo?>(project);
    }
}
