using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Project.Domain;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Workflow.Domain;
using System.Text.Json;

namespace Mohist.Server.Project.Services;

public class ProjectQuerier : ISingletonService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public ProjectQuerier(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<ProjectInfo>> ListAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entries = await db.Projects.OrderBy(p => p.Name).ToListAsync();
        var profiles = await LoadProjectVariablesAsync(db, entries.Select(e => e.Id));
        return entries.Select(e => ToInfo(e, profiles.GetValueOrDefault(e.Id))).ToList();
    }

    public async Task<ProjectInfo?> GetByIdAsync(string id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FindAsync(id);
        if (entry is null) return null;
        var variables = await LoadProjectVariablesAsync(db, entry.Id);
        return ToInfo(entry, variables);
    }

    public async Task<ProjectInfo?> GetByNameAsync(string name)
    {
        if (!ProjectName.TryNormalize(name, out var normalized, out _))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FirstOrDefaultAsync(p => p.Name == normalized);
        if (entry is null) return null;
        var variables = await LoadProjectVariablesAsync(db, entry.Id);
        return ToInfo(entry, variables);
    }

    public async Task<ProjectInfo?> ResolveByIdOrNameAsync(string identifier)
    {
        var normalizedName = ProjectName.TryNormalize(identifier, out var parsedName, out _)
            ? parsedName
            : null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FirstOrDefaultAsync(p => p.Id == identifier || (normalizedName != null && p.Name == normalizedName));
        if (entry is null) return null;
        var variables = await LoadProjectVariablesAsync(db, entry.Id);
        return ToInfo(entry, variables);
    }

    public async Task<bool> ExistsAsync(string name)
    {
        if (!ProjectName.TryNormalize(name, out var normalized, out _))
            return false;

        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Projects.AnyAsync(p => p.Name == normalized);
    }

    public async Task<ProjectInfo?> ResolveSingleAsync()
    {
        var all = await ListAllAsync();
        return all.Count == 1 ? all[0] : null;
    }

    private static async Task<Dictionary<string, ProjectVariablesBag>> LoadProjectVariablesAsync(
        MohistDbContext db,
        IEnumerable<string> projectIds)
    {
        var ids = projectIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return [];

        var rows = await db.ProjectWorkflowProfiles.AsNoTracking()
            .Where(p => ids.Contains(p.ProjectId))
            .Select(p => new { p.ProjectId, p.Variables })
            .ToListAsync();

        return rows.ToDictionary(
            row => row.ProjectId,
            row => ToProjectVariablesBag(row.Variables),
            StringComparer.Ordinal);
    }

    private static async Task<ProjectVariablesBag> LoadProjectVariablesAsync(MohistDbContext db, string projectId)
    {
        var variables = await db.ProjectWorkflowProfiles.AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .Select(p => p.Variables)
            .FirstOrDefaultAsync();
        return ToProjectVariablesBag(variables);
    }

    private static ProjectVariablesBag ToProjectVariablesBag(string? json)
    {
        var bundle = VariableBundle.FromJson(json);
        return new ProjectVariablesBag(
            ToDictionary(bundle.Vars),
            ToProjectStages(bundle.Stages));
    }

    private static Dictionary<string, ProjectStageVariablesBag?>? ToProjectStages(
        Dictionary<string, StageVariables>? stages)
    {
        if (stages is null || stages.Count == 0)
            return null;

        return stages.ToDictionary(
            kv => kv.Key,
            kv => (ProjectStageVariablesBag?)new ProjectStageVariablesBag(ToDictionary(kv.Value.Vars)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, JsonElement?>? ToDictionary(JsonElement? element)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
            return null;

        var result = element.Value.EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => (JsonElement?)property.Value.Clone(),
                StringComparer.Ordinal);

        return result.Count == 0 ? null : result;
    }

    internal static ProjectInfo ToInfo(ProjectRow e, ProjectVariablesBag? variables = null) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Repositories = JsonSerializer.Deserialize<List<RepositoryInfo>>(e.RepositoriesJson, JSON.Options) ?? [],
        Variables = variables ?? ProjectVariablesBag.Empty,
        CreatedAt = e.CreatedAt.ToString("o"),
        UpdatedAt = e.UpdatedAt.ToString("o"),
        DefaultExecutionConfig = ExecutionConfigJson.Deserialize(e.DefaultExecutionConfigJson),
    };
}
