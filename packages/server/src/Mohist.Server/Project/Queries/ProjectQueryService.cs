using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Storage;
using System.Text.Json;

namespace Mohist.Server.Project.Queries;

public class ProjectQueryService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public ProjectQueryService(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<ProjectInfo>> ListAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entries = await db.Projects.OrderBy(p => p.Name).ToListAsync();
        return entries.Select(ToInfo).ToList();
    }

    public async Task<ProjectInfo?> GetByIdAsync(string id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FindAsync(id);
        return entry is null ? null : ToInfo(entry);
    }

    public async Task<ProjectInfo?> GetByNameAsync(string name)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FirstOrDefaultAsync(p => p.Name == name);
        return entry is null ? null : ToInfo(entry);
    }

    public async Task<ProjectInfo?> ResolveByIdOrNameAsync(string identifier)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FirstOrDefaultAsync(p => p.Id == identifier || p.Name == identifier);
        return entry is null ? null : ToInfo(entry);
    }

    public async Task<bool> ExistsAsync(string name)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Projects.AnyAsync(p => p.Name == name);
    }

    public async Task<ProjectInfo?> ResolveSingleAsync()
    {
        var all = await ListAllAsync();
        return all.Count == 1 ? all[0] : null;
    }

    internal static ProjectInfo ToInfo(ProjectRow e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Path = e.Path,
        BaseBranch = e.BaseBranch,
        Repositories = JsonSerializer.Deserialize<List<RepositoryInfo>>(e.RepositoriesJson) ?? [],
        Variables = ProjectVariablesBag.FromJson(e.VariablesJson),
        CreatedAt = e.CreatedAt.ToString("o"),
        UpdatedAt = e.UpdatedAt.ToString("o"),
    };
}
