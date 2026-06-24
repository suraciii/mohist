using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain.IssueTemplate;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Issue.Services.IssueTemplates;

public class IssueTemplateRegistry : IScopedService
{
    private readonly Dictionary<string, IIssueTemplate> _builtins;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public IssueTemplateRegistry(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        var defaults = new MohistDefaultIssueTemplate();
        _builtins = new Dictionary<string, IIssueTemplate>(StringComparer.OrdinalIgnoreCase)
        {
            [defaults.Id] = defaults,
        };
    }

    public IIssueTemplate Get(string? id, string? projectId = null)
    {
        var templateId = string.IsNullOrWhiteSpace(id) ? IssueTemplates.DefaultId : id;

        if (_builtins.TryGetValue(templateId, out var builtin))
        {
            if (projectId is not null && IsDefaultDisabled(projectId))
                throw new KeyNotFoundException($"IssueTemplate '{templateId}' has been disabled for this project");
            return builtin;
        }

        if (projectId is not null)
        {
            var custom = LoadCustomTemplate(projectId, templateId);
            if (custom is not null) return custom;
        }

        throw new KeyNotFoundException($"IssueTemplate '{templateId}' not found");
    }

    public IReadOnlyList<IssueTemplateInfo> List(string? projectId = null)
    {
        var result = new List<IssueTemplateInfo>();

        if (projectId is null || !IsDefaultDisabled(projectId))
        {
            foreach (var builtin in _builtins.Values)
            {
                result.Add(ToInfo(builtin, "builtin"));
            }
        }

        if (projectId is not null)
        {
            var customs = LoadCustomTemplates(projectId);
            foreach (var custom in customs)
            {
                result.Add(ToInfo(custom, "custom"));
            }
        }

        result.Sort((a, b) =>
        {
            var defaultCmp = b.IsDefault.CompareTo(a.IsDefault);
            if (defaultCmp != 0) return defaultCmp;
            return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    public IReadOnlyList<IssueTemplateInfo> ListDescribed(string? projectId = null) =>
        List(projectId);

    public IssueTemplateInfo Default =>
        ToInfo(Get(IssueTemplates.DefaultId), "builtin");

    public bool Exists(string? id, string? projectId = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        if (_builtins.ContainsKey(id))
        {
            if (projectId is not null && IsDefaultDisabled(projectId))
                return false;
            return true;
        }

        if (projectId is not null)
        {
            var custom = LoadCustomTemplate(projectId, id);
            return custom is not null;
        }

        return false;
    }

    private static IssueTemplateInfo ToInfo(IIssueTemplate template, string source) =>
        new(template.Id, template.Name, template.About, template.IsDefault, template.SuitableFor, source);

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

    public bool Matches(string templateId, string? context, string? projectId = null) =>
        SuitableForMatcher.Matches(Get(templateId, projectId).SuitableFor, context);

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
        if (dto.About is null)
            throw new ArgumentException("Template is missing required field 'About'");
        if (dto.SuitableFor is null)
            throw new ArgumentException("Template is missing required field 'SuitableFor'");
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
    string About,
    bool IsDefault,
    IReadOnlyList<string> SuitableFor,
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
    public string About => _dto.About;
    public bool IsDefault => _dto.IsDefault;
    public IReadOnlyList<string> SuitableFor => _dto.SuitableFor;
    public IssueTemplateDefaults Defaults => new(
        _dto.Defaults?.Labels,
        _dto.Defaults?.Risk,
        _dto.Defaults?.Workflow);
    public IReadOnlyList<IssueTemplateSection> Sections => _dto.Sections
        .Select(s => new IssueTemplateSection(s.Title, s.Guidance, s.Placeholder))
        .ToList();
}
