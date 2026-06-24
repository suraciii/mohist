using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Label;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Label.Services;

public class LabelCatalogService : IScopedService
{
    private static readonly Regex LabelKeyPattern =
        new(@"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly SystemLabelDefinitions _systemDefinitions;

    public LabelCatalogService(
        IDbContextFactory<MohistDbContext> dbFactory,
        SystemLabelDefinitions systemDefinitions)
    {
        _dbFactory = dbFactory;
        _systemDefinitions = systemDefinitions;
    }

    public async Task<IReadOnlyList<LabelDefinition>> ListAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var userRows = await db.LabelDefinitions.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .ToListAsync();

        var userDefs = userRows.Select(ToDefinition).ToList();
        return MergeDefinitions(userDefs);
    }

    public async Task<LabelCreateResult> CreateAsync(
        string projectId,
        string key,
        string description,
        IReadOnlyList<string>? supportedValues = null)
    {
        var validationError = ValidateInput(key, description, supportedValues);
        if (validationError is not null)
            return new LabelCreateResult(Error: validationError);

        var systemKeys = GetSystemKeySet();
        if (systemKeys.Contains(key))
            return new LabelCreateResult(Error: $"Key '{key}' is reserved as a system definition and cannot be created.");

        await using var db = await _dbFactory.CreateDbContextAsync();

        var existing = await db.LabelDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Key == key);
        if (existing is not null)
            return new LabelCreateResult(Error: $"Key '{key}' already exists in the project catalog.");

        var now = DateTimeOffset.UtcNow;
        var row = new LabelDefinitionRow
        {
            Id = $"ldef_{Guid.NewGuid():N}",
            ProjectId = projectId,
            Key = key,
            Description = description,
            SupportedValuesJson = SerializeSupportedValues(supportedValues),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.LabelDefinitions.Add(row);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx
            && sqliteEx.SqliteErrorCode == 19)
        {
            return new LabelCreateResult(Error: $"Key '{key}' already exists in the project catalog.");
        }

        return new LabelCreateResult(Definition: ToDefinition(row));
    }

    public async Task<LabelUpdateResult> UpdateAsync(
        string projectId,
        string key,
        string description,
        IReadOnlyList<string>? supportedValues = null)
    {
        var validationError = ValidateInput(key, description, supportedValues);
        if (validationError is not null)
            return new LabelUpdateResult(Error: validationError);

        var systemKeys = GetSystemKeySet();
        if (systemKeys.Contains(key))
            return new LabelUpdateResult(Error: $"System definition '{key}' is immutable and cannot be modified.");

        await using var db = await _dbFactory.CreateDbContextAsync();

        var row = await db.LabelDefinitions
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Key == key);
        if (row is null)
            return new LabelUpdateResult(NotFound: true, Error: $"Key '{key}' not found in the project catalog.");

        row.Description = description;
        row.SupportedValuesJson = SerializeSupportedValues(supportedValues);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return new LabelUpdateResult(Definition: ToDefinition(row));
    }

    public async Task<LabelDeleteResult> DeleteAsync(string projectId, string key)
    {
        var systemKeys = GetSystemKeySet();
        if (systemKeys.Contains(key))
            return new LabelDeleteResult(Error: $"System definition '{key}' is immutable and cannot be removed.");

        await using var db = await _dbFactory.CreateDbContextAsync();

        var row = await db.LabelDefinitions
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Key == key);
        if (row is null)
            return new LabelDeleteResult(Error: null);

        db.LabelDefinitions.Remove(row);
        await db.SaveChangesAsync();

        return new LabelDeleteResult(Error: null);
    }

    private IReadOnlyList<LabelDefinition> MergeDefinitions(IReadOnlyList<LabelDefinition> userDefinitions)
    {
        var result = new List<LabelDefinition>();
        var keys = GetSystemKeySet();
        foreach (var sysDef in _systemDefinitions.All)
            result.Add(sysDef);

        foreach (var userDef in userDefinitions)
        {
            if (keys.Add(userDef.Key))
                result.Add(userDef);
        }

        return result;
    }

    private HashSet<string> GetSystemKeySet()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in _systemDefinitions.All)
            keys.Add(def.Key);
        return keys;
    }

    private static LabelDefinition ToDefinition(LabelDefinitionRow row)
    {
        return new LabelDefinition(
            Key: row.Key,
            Description: row.Description,
            Origin: LabelOrigin.User,
            SupportedValues: DeserializeSupportedValues(row.SupportedValuesJson));
    }

    private static string? ValidateInput(
        string key,
        string description,
        IReadOnlyList<string>? supportedValues)
    {
        if (string.IsNullOrEmpty(key) || !LabelKeyPattern.IsMatch(key))
            return $"Label key '{key}' is invalid; keys must match ^{LabelKeyPattern}$ (lowercase alphanumerics with optional interior dashes).";

        if (string.IsNullOrWhiteSpace(description))
            return "Description must be a non-empty, non-whitespace string.";

        if (supportedValues is not null)
        {
            foreach (var value in supportedValues)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return "Each supported value must be a non-empty, non-whitespace string.";
            }
        }

        return null;
    }

    private static string SerializeSupportedValues(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return "[]";
        return JSON.Serialize(values);
    }

    private static IReadOnlyList<string>? DeserializeSupportedValues(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return null;
        try
        {
            return JSON.Deserialize<List<string>>(json);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record LabelCreateResult(LabelDefinition? Definition = null, string? Error = null);
public sealed record LabelUpdateResult(LabelDefinition? Definition = null, string? Error = null, bool NotFound = false);
public sealed record LabelDeleteResult(string? Error = null);
