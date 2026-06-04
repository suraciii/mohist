using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Prompts.Domain;

namespace Mohist.Server.Workflow.Prompts.Storage;

public class ProjectTemplateStore : Mohist.Server.Workflow.Prompts.IProjectTemplateStore
{
    private static readonly JsonSerializerOptions TagsJsonOptions = new();

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public ProjectTemplateStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<ProjectTemplate>> GetForProjectAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.ProjectPromptTemplates
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync();
        return rows.Select(ToDomain).ToList();
    }

    public async Task<ProjectTemplate?> GetAsync(string projectId, string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectPromptTemplates
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Key == key);
        return row is null ? null : ToDomain(row);
    }

    public async Task<ProjectTemplate> UpsertAsync(
        string projectId,
        string key,
        string body,
        string displayName,
        string description,
        IReadOnlyList<string> tags,
        string? stage)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectPromptTemplates
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Key == key);
        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new ProjectTemplateRow
            {
                ProjectId = projectId,
                Key = key,
                DisplayName = displayName,
                Description = description,
                TagsJson = SerializeTags(tags),
                Stage = stage,
                Body = body,
                UpdatedAt = now,
            };
            db.ProjectPromptTemplates.Add(row);
        }
        else
        {
            row.DisplayName = displayName;
            row.Description = description;
            row.TagsJson = SerializeTags(tags);
            row.Stage = stage;
            row.Body = body;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync();
        return ToDomain(row);
    }

    public async Task DeleteAsync(string projectId, string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectPromptTemplates
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Key == key);
        if (row is null) return;
        db.ProjectPromptTemplates.Remove(row);
        await db.SaveChangesAsync();
    }

    private static ProjectTemplate ToDomain(ProjectTemplateRow row) => new(
        row.ProjectId,
        row.Key,
        row.DisplayName,
        row.Description,
        DeserializeTags(row.TagsJson),
        row.Stage,
        row.Body,
        row.UpdatedAt);

    private static string SerializeTags(IReadOnlyList<string> tags) =>
        JsonSerializer.Serialize(tags, TagsJsonOptions);

    private static IReadOnlyList<string> DeserializeTags(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, TagsJsonOptions)
                ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
