using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain.IssueTemplate;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.IssueTemplates;

public class IssueTemplateRegistry : IScopedService
{
    private const string AliasId = "mohist/default";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly Dictionary<string, BuiltinTemplateEntry> _builtinData;

    public IssueTemplateRegistry(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        var loader = new IssueTemplateFileLoader(
            Path.Combine(AppContext.BaseDirectory, "Issue/Services/IssueTemplates/templates"));
        _builtinData = loader.Discover();
    }

    internal IssueTemplateRegistry(IDbContextFactory<MohistDbContext> dbFactory, Dictionary<string, BuiltinTemplateEntry> builtinData)
    {
        _dbFactory = dbFactory;
        _builtinData = builtinData;
    }

    private string ResolveId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return IssueTemplates.DefaultId;
        if (string.Equals(id, AliasId, StringComparison.OrdinalIgnoreCase))
            return IssueTemplates.DefaultId;
        return id;
    }

    public IReadOnlyList<IssueTemplateInfo> List(string? projectId = null)
    {
        var result = new List<IssueTemplateInfo>();

        if (projectId is null || !IsDefaultDisabled(projectId))
        {
            foreach (var (id, entry) in _builtinData)
            {
                result.Add(new IssueTemplateInfo(id, entry.Name, entry.Description, "builtin"));
            }
        }

        if (projectId is not null)
        {
            var customs = LoadCustomTemplateInfos(projectId);
            foreach (var custom in customs)
            {
                result.Add(custom);
            }
        }

        result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

        return result;
    }

    public IIssueTemplate Get(string? id, string? projectId = null)
    {
        return GetWithSource(id, projectId).Template;
    }

    public IssueTemplateLookup GetWithSource(string? id, string? projectId = null)
    {
        var resolvedId = ResolveId(id);

        var defaultDisabled = projectId is not null && IsDefaultDisabled(projectId);

        if (defaultDisabled && projectId is not null)
        {
            var custom = LoadCustomTemplate(projectId, resolvedId);
            if (custom is not null) return new IssueTemplateLookup(custom, "custom");
        }

        if (_builtinData.TryGetValue(resolvedId, out var entry))
        {
            if (defaultDisabled)
                throw new KeyNotFoundException($"IssueTemplate '{resolvedId}' has been disabled for this project");

            try
            {
                var (_, body) = PromptFrontmatterParser.Parse(entry.LoadContent(), resolvedId);
                return new IssueTemplateLookup(
                    new FileAssetIssueTemplate(resolvedId, entry.Name, entry.Description, body),
                    "builtin");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to load issue template '{resolvedId}' from '{entry.FilePath}'.", ex);
            }
        }

        if (projectId is not null)
        {
            var custom = LoadCustomTemplate(projectId, resolvedId);
            if (custom is not null) return new IssueTemplateLookup(custom, "custom");
        }

        throw new KeyNotFoundException($"IssueTemplate '{resolvedId}' not found");
    }

    public IReadOnlyList<IssueTemplateInfo> ListDescribed(string? projectId = null) =>
        List(projectId);

    public IssueTemplateInfo Default
    {
        get
        {
            var entry = _builtinData[IssueTemplates.DefaultId];
            return new IssueTemplateInfo(IssueTemplates.DefaultId, entry.Name, entry.Description, "builtin");
        }
    }

    public bool Exists(string? id, string? projectId = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var resolvedId = ResolveId(id);
        var defaultDisabled = projectId is not null && IsDefaultDisabled(projectId);

        if (defaultDisabled && projectId is not null)
        {
            var custom = LoadCustomTemplate(projectId, resolvedId);
            if (custom is not null) return true;
        }

        if (_builtinData.ContainsKey(resolvedId))
        {
            if (defaultDisabled)
                return false;
            return true;
        }

        if (projectId is not null)
        {
            var custom = LoadCustomTemplate(projectId, resolvedId);
            return custom is not null;
        }

        return false;
    }

    public bool IsBuiltin(string id) =>
        _builtinData.ContainsKey(ResolveId(id));

    private bool IsDefaultDisabled(string projectId)
    {
        using var db = _dbFactory.CreateDbContext();
        var profile = db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefault(x => x.ProjectId == projectId);
        return profile?.DisableDefaultIssueTemplate == true;
    }

    private IIssueTemplate? LoadCustomTemplate(string projectId, string name)
    {
        using var db = _dbFactory.CreateDbContext();
        var row = db.ProjectIssueTemplates.AsNoTracking()
            .FirstOrDefault(x => x.ProjectId == projectId && x.Name == name);
        if (row is null || string.IsNullOrEmpty(row.Template)) return null;

        try
        {
            return DeserializeTemplate(row.Template, row.Name);
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<IssueTemplateInfo> LoadCustomTemplateInfos(string projectId)
    {
        using var db = _dbFactory.CreateDbContext();
        var rows = db.ProjectIssueTemplates.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .ToList();

        var result = new List<IssueTemplateInfo>();
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Template)) continue;
            try
            {
                var info = DeserializeTemplateInfo(row.Template, row.Name);
                result.Add(info);
            }
            catch
            {
            }
        }
        return result;
    }

    private static IssueTemplateInfo DeserializeTemplateInfo(string json, string rowName)
    {
        var dto = JSON.DeserializeOrThrow<IssueTemplateMetadataDto>(json);
        ValidateTemplateMetadata(dto, rowName);
        return new IssueTemplateInfo(dto.Id, dto.Name, DeserializedDescription(dto), "custom");
    }

    private static IIssueTemplate DeserializeTemplate(string json, string rowName)
    {
        var dto = JSON.DeserializeOrThrow<IssueTemplateDto>(json);
        ValidateTemplateMetadata(dto, rowName);
        var body = ComposeBodyFromSections(dto);
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Template must have a non-empty body");
        return new DeserializedIssueTemplate(dto.Id, dto.Name, DeserializedDescription(dto), body);
    }

    private static string ComposeBodyFromSections(IssueTemplateDto dto)
    {
        if (dto.Sections is null || dto.Sections.Count == 0) return string.Empty;
        return string.Join("\n\n", dto.Sections.Select(s => $"## {s.Title}\n{s.Placeholder}"));
    }

    private static string DeserializedDescription(IssueTemplateMetadataDto dto) =>
        string.IsNullOrWhiteSpace(dto.Description)
            ? (string.IsNullOrWhiteSpace(dto.About) ? string.Empty : dto.About)
            : dto.Description;

    private static void ValidateTemplateMetadata(IssueTemplateMetadataDto dto, string rowName)
    {
        if (string.IsNullOrWhiteSpace(dto.Id))
            throw new ArgumentException("Template is missing required field 'Id'");
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new ArgumentException("Template is missing required field 'Name'");
        if (!string.Equals(dto.Id, rowName, StringComparison.Ordinal))
            throw new ArgumentException("Template id must match row name");
    }
}

public sealed record IssueTemplateInfo(
    string Id,
    string Name,
    string Description,
    string Source);

public sealed record IssueTemplateLookup(IIssueTemplate Template, string Source);

public class IssueTemplateMetadataDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string About { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class IssueTemplateDto : IssueTemplateMetadataDto
{
    public bool IsDefault { get; set; }
    public List<string> SuitableFor { get; set; } = new();
    public IssueTemplateDefaultsDto? Defaults { get; set; }
    public List<IssueTemplateSectionDto> Sections { get; set; } = new();
}

public class IssueTemplateDefaultsDto
{
    public Dictionary<string, string>? Labels { get; set; }
    public string? Risk { get; set; }
    public string? Workflow { get; set; }
}

public class IssueTemplateSectionDto
{
    public string Title { get; set; } = string.Empty;
    public string Guidance { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
}

internal sealed class DeserializedIssueTemplate : IIssueTemplate
{
    public DeserializedIssueTemplate(string id, string name, string description, string body)
    {
        Id = id;
        Name = name;
        Description = description;
        Body = body;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Body { get; }
}

internal sealed class FileAssetIssueTemplate : IIssueTemplate
{
    public FileAssetIssueTemplate(string id, string name, string description, string body)
    {
        Id = id;
        Name = name;
        Description = description;
        Body = body;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Body { get; }
}
