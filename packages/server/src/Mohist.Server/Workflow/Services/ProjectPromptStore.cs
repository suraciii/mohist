using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Prompts;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Workflow.Services;

public class ProjectPromptStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptTemplateEngine _engine;

    public ProjectPromptStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        IPromptLoader promptLoader,
        PromptTemplateEngine engine)
    {
        _dbFactory = dbFactory;
        _promptLoader = promptLoader;
        _engine = engine;
    }

    public async Task<IReadOnlyList<EffectivePrompt>> ListPromptsAsync(
        string projectId,
        string? stage = null)
    {
        var systemTemplates = _promptLoader.LoadAllTemplates();
        var projectPrompts = await LoadProjectPromptsAsync(projectId);
        var keys = new SortedSet<string>(systemTemplates.Keys, StringComparer.Ordinal);
        keys.UnionWith(projectPrompts.Keys);

        var prompts = keys.Select(key => ResolvePrompt(systemTemplates, projectPrompts, key)).ToList();
        return string.IsNullOrWhiteSpace(stage)
            ? prompts
            : prompts.Where(prompt => prompt.Stage is null
                || string.Equals(prompt.Stage, stage, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<EffectivePrompt?> GetPromptAsync(string projectId, string key)
    {
        var systemTemplates = _promptLoader.LoadAllTemplates();
        var projectPrompts = await LoadProjectPromptsAsync(projectId);
        return projectPrompts.ContainsKey(key) || systemTemplates.ContainsKey(key)
            ? ResolvePrompt(systemTemplates, projectPrompts, key)
            : null;
    }

    public async Task SetPromptAsync(string projectId, string key, string body)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);

        if (profile is null)
        {
            profile = new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                Variables = VariableBundle.Empty.ToJson(),
                Prompts = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = body },
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.ProjectWorkflowProfiles.Add(profile);
        }
        else
        {
            var prompts = new Dictionary<string, string>(profile.Prompts, StringComparer.Ordinal)
            {
                [key] = body,
            };
            profile.Prompts = prompts;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public async Task DeletePromptAsync(string projectId, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        if (profile is null) return;

        var prompts = new Dictionary<string, string>(profile.Prompts, StringComparer.Ordinal);
        if (!prompts.Remove(key)) return;
        profile.Prompts = prompts;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<PromptPreviewResult> PreviewPromptAsync(
        string projectId,
        string key,
        JsonElement variables)
    {
        var prompt = await GetPromptAsync(projectId, key)
            ?? throw new ArgumentException($"Prompt '{key}' not found");
        return RenderPrompt(prompt.Body, variables);
    }

    public PromptPreviewResult RenderPrompt(string body, JsonElement variables)
    {
        var result = _engine.Render(body, variables);
        return new PromptPreviewResult(result.Rendered, result.MissingVariables, result.Depth, result.Errors);
    }

    public async Task<Dictionary<string, string>> GetMergedPromptBodiesAsync(string projectId)
    {
        var merged = new Dictionary<string, string>(_promptLoader.LoadAll(), StringComparer.Ordinal);
        var projectPrompts = await LoadProjectPromptsAsync(projectId);
        foreach (var (key, body) in projectPrompts)
            merged[key] = body;
        return merged;
    }

    private async Task<Dictionary<string, string>> LoadProjectPromptsAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        return profile?.Prompts ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static EffectivePrompt ResolvePrompt(
        IReadOnlyDictionary<string, SystemTemplate> systemTemplates,
        IReadOnlyDictionary<string, string> projectPrompts,
        string key)
    {
        if (projectPrompts.TryGetValue(key, out var body))
        {
            var source = systemTemplates.ContainsKey(key) ? "project" : "project-new";
            return new EffectivePrompt(key, key, string.Empty, Array.Empty<string>(), null, body, source);
        }

        var system = systemTemplates[key];
        return new EffectivePrompt(
            key,
            system.DisplayName,
            system.Description,
            system.Tags,
            system.Stage,
            system.Body,
            "system");
    }
}
