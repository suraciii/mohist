using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Project.Domain;

namespace Mohist.Server.Infrastructure.Data.Project;

public static class ProjectRepositoryDataUpgrader
{
    private static readonly HashSet<string> RepairableValidationCodes =
    [
        "repository_default_missing",
        "repository_default_multiple",
    ];

    public static async Task UpgradeAsync(MohistDbContext db, CancellationToken cancellationToken = default)
    {
        var projects = await db.Projects
            .OrderBy(project => project.Id)
            .ToListAsync(cancellationToken);
        var upgrades = new List<(ProjectRow Project, string RepositoriesJson)>();
        var diagnostics = new List<string>();

        foreach (var project in projects)
        {
            if (!TryPrepareUpgrade(project, out var repositoriesJson, out var diagnostic))
            {
                diagnostics.Add(diagnostic);
                continue;
            }

            if (repositoriesJson is not null)
                upgrades.Add((project, repositoriesJson));
        }

        if (diagnostics.Count > 0)
        {
            throw new InvalidOperationException(
                "Project repository data upgrade failed:\n" + string.Join("\n", diagnostics));
        }

        if (upgrades.Count == 0)
            return;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var upgrade in upgrades)
            upgrade.Project.RepositoriesJson = upgrade.RepositoriesJson;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool TryPrepareUpgrade(
        ProjectRow project,
        out string? repositoriesJson,
        out string diagnostic)
    {
        repositoriesJson = null;
        diagnostic = string.Empty;
        List<RepositoryInfo>? declarations;

        try
        {
            declarations = JsonSerializer.Deserialize<List<RepositoryInfo>>(
                project.RepositoriesJson,
                JSON.Options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            diagnostic = FormatDiagnostic(project, $"RepositoriesJson is malformed: {exception.Message}");
            return false;
        }

        if (declarations?.Any(declaration => declaration is null) == true)
        {
            diagnostic = FormatDiagnostic(project, "RepositoriesJson contains a null repository declaration.");
            return false;
        }

        var repositories = declarations?
            .Select(declaration => new RepositoryPolicy.NormalizedRepository(
                declaration.Name,
                declaration.GitUrl,
                declaration.BaseBranch,
                declaration.IsDefault))
            .ToList() ?? [];
        var validationErrors = RepositoryPolicy.Validate(repositories)
            .Where(error => !RepairableValidationCodes.Contains(error.Code))
            .ToList();

        if (validationErrors.Count > 0)
        {
            diagnostic = FormatDiagnostic(
                project,
                string.Join(" ", validationErrors.Select(error => error.Message)));
            return false;
        }

        var normalized = RepositoryPolicy.Normalize(repositories);
        var normalizedErrors = RepositoryPolicy.Validate(normalized);
        if (normalizedErrors.Count > 0)
        {
            diagnostic = FormatDiagnostic(
                project,
                string.Join(" ", normalizedErrors.Select(error => error.Message)));
            return false;
        }

        var normalizedDeclarations = normalized
            .Select(repository => new RepositoryInfo
            {
                Name = repository.Name,
                GitUrl = repository.GitUrl,
                BaseBranch = repository.BaseBranch,
                IsDefault = repository.IsDefault,
            })
            .ToList();
        var normalizedJson = JSON.Serialize(normalizedDeclarations);
        if (!RepositoryListsEqual(repositories, normalized))
            repositoriesJson = normalizedJson;
        return true;
    }

    private static bool RepositoryListsEqual(
        IReadOnlyList<RepositoryPolicy.NormalizedRepository> current,
        IReadOnlyList<RepositoryPolicy.NormalizedRepository> normalized) =>
        current.Count == normalized.Count
        && current.Zip(normalized).All(pair => pair.First == pair.Second);

    private static string FormatDiagnostic(ProjectRow project, string message) =>
        $"Project '{project.Name}' ({project.Id}): {message}";
}
