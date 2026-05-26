using ProjectInfo = Mohist.Server.Project.Queries.ProjectInfo;
using Mohist.Server.Storage;

namespace Mohist.Server.Project.Grains;

public class ProjectGrain : Grain, IProjectGrain
{
    private readonly Dictionary<string, ProjectInfo> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly IStateStore<ProjectState> _store;
    private readonly ILogger<ProjectGrain> _log;

    public ProjectGrain(IStateStore<ProjectState> store, ILogger<ProjectGrain> log)
    {
        _store = store;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        var state = await _store.LoadAsync(GrainKey);
        if (state is null) return;
        foreach (var (name, project) in state.Projects)
            _projects[name] = project;
    }

    public Task<ProjectInfo?> GetByNameAsync(string name)
    {
        _projects.TryGetValue(name, out var project);
        return Task.FromResult(project);
    }

    public Task<ProjectInfo?> GetByIdAsync(string id)
    {
        var project = _projects.Values.FirstOrDefault(p => p.Id == id);
        return Task.FromResult(project);
    }

    public Task<List<ProjectInfo>> GetAllAsync()
    {
        return Task.FromResult(_projects.Values.ToList());
    }

    public async Task<ProjectInfo> CreateAsync(string name, string path, string? baseBranch)
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
        await SaveAsync();
        _log.LogInformation("Project {Name} created at {Path}", name, path);
        return project;
    }

    public async Task<ProjectInfo?> UpdateAsync(string name, string? baseBranch)
    {
        if (!_projects.TryGetValue(name, out var project))
            return null;

        if (baseBranch is not null)
            project.BaseBranch = baseBranch;

        project.UpdatedAt = DateTime.UtcNow.ToString("o");
        await SaveAsync();
        return project;
    }

    public async Task<bool> DeleteAsync(string name)
    {
        var removed = _projects.Remove(name);
        if (removed)
            await SaveAsync();
        return removed;
    }

    private Task SaveAsync() => _store.SaveAsync(GrainKey, new ProjectState(new Dictionary<string, ProjectInfo>(_projects, StringComparer.OrdinalIgnoreCase)));
}
