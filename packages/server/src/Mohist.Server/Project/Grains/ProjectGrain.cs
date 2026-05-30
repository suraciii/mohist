using Microsoft.EntityFrameworkCore;
using Mohist.Server.Project.Queries;
using Mohist.Server.Project.Storage;
using Mohist.Server.Infrastructure.Persistence.Db;
using System.Text.Json;

namespace Mohist.Server.Project.Grains;

public class ProjectGrain : Grain, IProjectGrain
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ILogger<ProjectGrain> _log;
    private ProjectInfo? _project;

    public ProjectGrain(IDbContextFactory<MohistDbContext> dbFactory, ILogger<ProjectGrain> log)
    {
        _dbFactory = dbFactory;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FindAsync(GrainKey);
        if (entry is not null)
            _project = ProjectQueryService.ToInfo(entry);
    }

    public Task<ProjectInfo?> GetAsync() => Task.FromResult(_project);

    public async Task<ProjectInfo> CreateAsync(string name, string path, string? baseBranch)
    {
        if (_project is not null)
            throw new InvalidOperationException($"Project '{GrainKey}' already exists");

        var branch = baseBranch ?? "main";
        var repos = new List<RepositoryInfo>
        {
            new()
            {
                Name = "main",
                Path = path,
                BaseBranch = branch,
                IsDefault = true,
            },
        };

        var entry = new ProjectRow
        {
            Id = GrainKey,
            Name = name,
            Path = path,
            BaseBranch = branch,
            RepositoriesJson = JsonSerializer.Serialize(repos),
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Projects.Add(entry);
        await db.SaveChangesAsync();

        _project = new ProjectInfo
        {
            Id = GrainKey,
            Name = name,
            Path = path,
            BaseBranch = branch,
            Repositories = repos,
        };

        _log.LogInformation("Project {Name} created at {Path}", name, path);
        return _project;
    }

    public async Task<ProjectInfo?> UpdateAsync(string? baseBranch)
    {
        if (_project is null) return null;

        if (baseBranch is not null)
            _project.BaseBranch = baseBranch;

        _project.UpdatedAt = DateTime.UtcNow.ToString("o");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FindAsync(GrainKey);
        if (entry is null) return null;

        entry.BaseBranch = _project.BaseBranch;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return _project;
    }

    public async Task DeleteAsync()
    {
        if (_project is null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FindAsync(GrainKey);
        if (entry is not null)
        {
            db.Projects.Remove(entry);
            await db.SaveChangesAsync();
        }

        _project = null;
    }

    public Task<List<RepositoryInfo>> ListRepositoriesAsync()
    {
        return Task.FromResult(_project?.Repositories ?? []);
    }

    public async Task<ProjectInfo?> AddRepositoryAsync(string repoName, string? path, string? remote, string? baseBranch)
    {
        if (_project is null) return null;
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(remote))
            throw new InvalidOperationException("path or remote is required");
        if (_project.Repositories.Any(r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Repository '{repoName}' already exists");

        _project.Repositories.Add(new RepositoryInfo
        {
            Name = repoName,
            Path = path,
            Remote = remote,
            BaseBranch = baseBranch ?? "main",
            IsDefault = _project.Repositories.Count == 0,
        });

        _project.UpdatedAt = DateTime.UtcNow.ToString("o");
        await PersistRepositoriesAsync();
        return _project;
    }

    public async Task<ProjectInfo?> RemoveRepositoryAsync(string repoName)
    {
        if (_project is null) return null;

        var repo = _project.Repositories.FirstOrDefault(r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase));
        if (repo is null) return null;

        _project.Repositories.Remove(repo);
        if (repo.IsDefault && _project.Repositories.Count > 0)
            _project.Repositories[0].IsDefault = true;

        _project.UpdatedAt = DateTime.UtcNow.ToString("o");
        await PersistRepositoriesAsync();
        return _project;
    }

    public async Task<ProjectInfo?> SetDefaultRepositoryAsync(string repoName)
    {
        if (_project is null) return null;

        var found = false;
        foreach (var repo in _project.Repositories)
        {
            if (string.Equals(repo.Name, repoName, StringComparison.OrdinalIgnoreCase))
            {
                repo.IsDefault = true;
                found = true;
            }
            else
            {
                repo.IsDefault = false;
            }
        }

        if (!found) return null;

        _project.UpdatedAt = DateTime.UtcNow.ToString("o");
        await PersistRepositoriesAsync();
        return _project;
    }

    private async Task PersistRepositoriesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FindAsync(GrainKey);
        if (entry is null) return;

        entry.RepositoriesJson = JsonSerializer.Serialize(_project!.Repositories);
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}
