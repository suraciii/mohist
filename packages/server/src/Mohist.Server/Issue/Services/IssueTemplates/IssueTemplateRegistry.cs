using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain.IssueTemplate;

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
        _builtinData = loader.Load();
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
            var customs = LoadCustomTemplates(projectId);
            foreach (var custom in customs)
            {
                result.Add(new IssueTemplateInfo(custom.Id, custom.Name, custom.Description, "custom"));
            }
        }

        result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

        return result;
    }

    public IIssueTemplate Get(string? id, string? projectId = null)
    {
        var resolvedId = ResolveId(id);

        if (_builtinData.TryGetValue(resolvedId, out var entry))
        {
            if (projectId is not null && IsDefaultDisabled(projectId))
                throw new KeyNotFoundException($"IssueTemplate '{resolvedId}' has been disabled for this project");

            var sections = IssueTemplateBodyParser.Parse(entry.Body);
            return new FileAssetIssueTemplate(resolvedId, entry.Name, entry.Description, sections);
        }

        if (projectId is not null)
        {
            var custom = LoadCustomTemplate(projectId, resolvedId);
            if (custom is not null) return custom;
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

        if (_builtinData.ContainsKey(resolvedId))
        {
            if (projectId is not null && IsDefaultDisabled(projectId))
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

    private IReadOnlyList<IIssueTemplate> LoadCustomTemplates(string projectId)
    {
        using var db = _dbFactory.CreateDbContext();
        var rows = db.ProjectIssueTemplates.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .ToList();

        var result = new List<IIssueTemplate>();
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Template)) continue;
            try
            {
                var template = DeserializeTemplate(row.Template, row.Name);
                result.Add(template);
            }
            catch
            {
                // Invalid template — skip
            }
        }
        return result;
    }

    private static IIssueTemplate DeserializeTemplate(string json, string rowName)
    {
        var dto = JSON.DeserializeOrThrow<IssueTemplateDto>(json);
        ValidateTemplate(dto, rowName);
        return new DeserializedIssueTemplate(dto);
    }

    private static void ValidateTemplate(IssueTemplateDto dto, string rowName)
    {
        if (string.IsNullOrWhiteSpace(dto.Id))
            throw new ArgumentException("Template is missing required field 'Id'");
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new ArgumentException("Template is missing required field 'Name'");
        if (dto.Sections is null || dto.Sections.Count == 0)
            throw new ArgumentException("Template must have at least one section");
        if (!string.Equals(dto.Id, rowName, StringComparison.Ordinal))
            throw new ArgumentException("Template id must match row name");

        foreach (var section in dto.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Title))
                throw new ArgumentException("Template section is missing required field 'Title'");
            if (string.IsNullOrWhiteSpace(section.Guidance))
                throw new ArgumentException("Template section is missing required field 'Guidance'");
            if (string.IsNullOrWhiteSpace(section.Placeholder))
                throw new ArgumentException("Template section is missing required field 'Placeholder'");
        }
    }
}

public sealed record IssueTemplateInfo(
    string Id,
    string Name,
    string Description,
    string Source);

public class IssueTemplateDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string About { get; set; } = string.Empty;
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
    private readonly IssueTemplateDto _dto;

    public DeserializedIssueTemplate(IssueTemplateDto dto)
    {
        _dto = dto;
    }

    public string Id => _dto.Id;
    public string Name => _dto.Name;
    public string Description => string.IsNullOrWhiteSpace(_dto.About) ? string.Empty : _dto.About;
    public IReadOnlyList<IssueTemplateSection> Sections => _dto.Sections
        .Select(s => new IssueTemplateSection(s.Title, s.Guidance, s.Placeholder))
        .ToList();
}

internal sealed class FileAssetIssueTemplate : IIssueTemplate
{
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<IssueTemplateSection> Sections { get; }

    public FileAssetIssueTemplate(string id, string name, string description, IReadOnlyList<IssueTemplateSection> sections)
    {
        Id = id;
        Name = name;
        Description = description;
        Sections = sections;
    }
}
