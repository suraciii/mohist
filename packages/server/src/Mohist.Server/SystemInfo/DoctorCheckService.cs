using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Grains;
using Orleans;

namespace Mohist.Server.SystemInfo;

public sealed record DoctorCheck(
    string Name,
    string Status,
    string Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextAction);

public sealed record DoctorRevisionFacts(
    IReadOnlyDictionary<string, string?> Revisions);

/// <summary>
/// One Project's verification-relevant facts, read once per doctor evaluation.
/// The classification into required vs optional is a pure function of these
/// facts and is performed by <see cref="DoctorCheckService"/>.
/// </summary>
public sealed record DoctorProjectFact(
    string Name,
    bool HasVerificationCommand,
    bool HasActiveExecution,
    bool IsOptional);

public sealed record DoctorFactSnapshot(
    DoctorRevisionFacts Revision,
    bool MigrationsCurrent,
    IReadOnlyList<DoctorProjectFact> Projects,
    IReadOnlyList<string> IncompleteRuntimeCatalogs);

public interface IDoctorFactSource
{
    Task<DoctorRevisionFacts> GetRevisionFactsAsync(CancellationToken ct);
    Task<bool> AreMigrationsCurrentAsync(CancellationToken ct);
    Task<IReadOnlyList<DoctorProjectFact>> GetProjectFactsAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> GetIncompleteRuntimeCatalogsAsync(CancellationToken ct);
}

public sealed class DoctorFactSource : IDoctorFactSource, IScopedService
{
    private const string OptionalProjectsConfigurationKey = "Mohist:Doctor:OptionalProjects";

    private readonly IRuntimeBuildInfo _runtime;
    private readonly IGrainFactory _grains;
    private readonly IDbContextFactory<MohistDbContext> _db;
    private readonly ProjectQuerier _projects;
    private readonly IConfiguration _configuration;

    public DoctorFactSource(
        IRuntimeBuildInfo runtime,
        IGrainFactory grains,
        IDbContextFactory<MohistDbContext> db,
        ProjectQuerier projects,
        IConfiguration configuration)
    {
        _runtime = runtime;
        _grains = grains;
        _db = db;
        _projects = projects;
        _configuration = configuration;
    }

    public async Task<DoctorRevisionFacts> GetRevisionFactsAsync(CancellationToken ct)
    {
        var revisions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["server"] = RevisionOf(_runtime.SourceRevision ?? _runtime.GitHash),
            ["cli"] = RevisionOf(_configuration["Mohist:Doctor:Revisions:cli"]),
            ["slack"] = RevisionOf(_configuration["Mohist:Doctor:Revisions:slack"]),
        };

        var runners = await _grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).ListAllAsync();
        foreach (var runner in runners)
        {
            var revision = RevisionOf(runner.SourceRevision ?? runner.BuildGitHash);
            if (revision is not null)
                revisions[$"runner:{runner.RunnerId}"] = revision;
        }

        return new DoctorRevisionFacts(revisions);
    }

    public async Task<bool> AreMigrationsCurrentAsync(CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return !(await db.Database.GetPendingMigrationsAsync(ct)).Any();
    }

    public async Task<IReadOnlyList<DoctorProjectFact>> GetProjectFactsAsync(CancellationToken ct)
    {
        var projects = await _projects.ListAllAsync();
        var activeProjectIds = await GetActiveProjectIdsAsync(ct);
        var optionalEntries = _configuration.GetSection(OptionalProjectsConfigurationKey).Get<string[]>() ?? [];

        return projects
            .Select(project => new DoctorProjectFact(
                project.Name,
                HasVerificationCommand: !string.IsNullOrWhiteSpace(project.VerificationCommand),
                HasActiveExecution: activeProjectIds.Contains(project.Id),
                IsOptional: IsExplicitlyOptional(project, optionalEntries)))
            .ToArray();
    }

    private async Task<HashSet<string>> GetActiveProjectIdsAsync(CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        // Terminal statuses whose runs no longer require a verification command.
        // Keep in sync with WorkflowRunStatusExtensions.IsTerminal: `Failed` is
        // deliberately absent because Retry/Rerun can revive it. The status
        // column is the stored lowercase projection, so filter at the DB layer.
        var active = await db.WorkflowRuns.AsNoTracking()
            .Where(run => run.MetadataProjectId != null
                && run.Status != "completed"
                && run.Status != "stopped")
            .Select(run => run.MetadataProjectId!)
            .Distinct()
            .ToListAsync(ct);
        return active.ToHashSet(StringComparer.Ordinal);
    }

    internal static bool IsExplicitlyOptional(ProjectInfo project, IEnumerable<string> optionalEntries)
    {
        foreach (var entry in optionalEntries)
        {
            var candidate = entry?.Trim();
            if (string.IsNullOrEmpty(candidate))
                continue;

            if (string.Equals(candidate, project.Id, StringComparison.Ordinal)
                || string.Equals(candidate, project.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public async Task<IReadOnlyList<string>> GetIncompleteRuntimeCatalogsAsync(CancellationToken ct)
    {
        var runners = await _grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).ListAllAsync();
        var incomplete = new List<string>();
        foreach (var runner in runners)
        {
            if (runner.RuntimeCatalogs is null || runner.RuntimeCatalogs.Count == 0)
            {
                incomplete.Add(runner.RunnerId);
                continue;
            }

            foreach (var catalog in runner.RuntimeCatalogs)
            {
                if (catalog.Value is null
                    || catalog.Value.Complete != true
                    || catalog.Value.Models is not { Length: > 0 })
                    incomplete.Add($"{runner.RunnerId}:{catalog.Key}");
            }
        }

        return incomplete;
    }

    private static string? RevisionOf(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class DoctorCheckService : IScopedService
{
    public const string RevisionAlignment = "revision-alignment";
    public const string Migrations = "migrations";
    public const string ModelCatalog = "model-catalog";
    public const string VerificationCommand = "verification-command";
    public const string ProjectVerificationOptional = "project-verification-optional";

    private const string SetVerificationCommand = "mo project workflow verification set";

    private readonly IDoctorFactSource _facts;

    public DoctorCheckService(IDoctorFactSource facts)
    {
        _facts = facts;
    }

    public async Task<IReadOnlyList<DoctorCheck>> GetChecksAsync(CancellationToken ct = default, bool strict = false)
    {
        var revision = await EvaluateAsync(RevisionAlignment, () => _facts.GetRevisionFactsAsync(ct), EvaluateRevision);
        var migrations = await EvaluateAsync(Migrations, () => _facts.AreMigrationsCurrentAsync(ct), EvaluateMigrations);
        var catalog = await EvaluateAsync(ModelCatalog, () => _facts.GetIncompleteRuntimeCatalogsAsync(ct), EvaluateCatalog);
        var (verification, optional) = await EvaluateProjectsAsync(ct, strict);

        return [revision, migrations, catalog, verification, optional];
    }

    public static IReadOnlyList<DoctorCheck> Evaluate(DoctorFactSnapshot facts, bool strict = false)
    {
        var (verification, optional) = EvaluateProjects(facts.Projects, strict);
        return
        [
            EvaluateRevision(facts.Revision),
            EvaluateMigrations(facts.MigrationsCurrent),
            EvaluateCatalog(facts.IncompleteRuntimeCatalogs),
            verification,
            optional,
        ];
    }

    private async Task<(DoctorCheck Verification, DoctorCheck Optional)> EvaluateProjectsAsync(
        CancellationToken ct,
        bool strict)
    {
        try
        {
            return EvaluateProjects(await _facts.GetProjectFactsAsync(ct), strict);
        }
        catch (Exception ex)
        {
            var detail = $"Unable to read project verification facts: {ex.Message}";
            const string nextAction = "Repair the project verification fact source and run mo doctor again.";
            return (
                Fail(VerificationCommand, detail, nextAction),
                Fail(ProjectVerificationOptional, detail, nextAction));
        }
    }

    private static async Task<DoctorCheck> EvaluateAsync<T>(
        string name,
        Func<Task<T>> read,
        Func<T, DoctorCheck> evaluate)
    {
        try
        {
            return evaluate(await read());
        }
        catch (Exception ex)
        {
            return Fail(name, $"Unable to read {name} facts: {ex.Message}", $"Repair the {name} fact source and run mo doctor again.");
        }
    }

    private static DoctorCheck EvaluateRevision(DoctorRevisionFacts facts)
    {
        var known = facts.Revisions.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToArray();
        var distinct = known.Select(pair => pair.Value!).Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length <= 1
            ? new DoctorCheck(RevisionAlignment, "ok", "Known component revisions are aligned", null)
            : Fail(RevisionAlignment, $"Component revisions differ: {string.Join(", ", known.Select(pair => $"{pair.Key}={pair.Value}"))}", "Deploy the same revision to CLI, Server, Runner, and Slack, then run mo doctor again.");
    }

    private static DoctorCheck EvaluateMigrations(bool current) =>
        current
            ? new DoctorCheck(Migrations, "ok", "Database schema is at the current migration boundary", null)
            : Fail(Migrations, "Database has pending migrations", "Run the Server database migration before starting workflows.");

    private static (DoctorCheck Verification, DoctorCheck Optional) EvaluateProjects(
        IReadOnlyList<DoctorProjectFact> projects,
        bool strict)
    {
        var requiredMissing = new SortedSet<string>(StringComparer.Ordinal);
        var optionalMissing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            if (project.HasVerificationCommand)
                continue;

            // strict promotes every missing Project to required; otherwise a
            // Project is required only when it has active execution and is not
            // explicitly optional.
            if (strict || (project.HasActiveExecution && !project.IsOptional))
                requiredMissing.Add(project.Name);
            else
                optionalMissing.Add(project.Name);
        }

        var verification = requiredMissing.Count == 0
            ? new DoctorCheck(VerificationCommand, "ok", "All required Projects have a verification command", null)
            : Fail(
                VerificationCommand,
                $"Required Projects missing verification commands: {string.Join(", ", requiredMissing)}",
                $"Set a verification command for each listed Project with {SetVerificationCommand}.");

        var optional = optionalMissing.Count == 0
            ? new DoctorCheck(ProjectVerificationOptional, "ok", "All optional Projects have a verification command", null)
            : new DoctorCheck(
                ProjectVerificationOptional,
                "warn",
                $"Optional Projects missing verification commands: {string.Join(", ", optionalMissing)}",
                $"Set a verification command for each listed Project with {SetVerificationCommand}.");

        return (verification, optional);
    }

    private static DoctorCheck EvaluateCatalog(IReadOnlyList<string> incomplete) =>
        incomplete.Count == 0
            ? new DoctorCheck(ModelCatalog, "ok", "All discovered runtime catalogs are complete", null)
            : Fail(ModelCatalog, $"Runtime catalogs are empty or incomplete: {string.Join(", ", incomplete)}", "Reconnect or refresh the affected Runner runtime catalogs, then run mo doctor again.");

    private static DoctorCheck Fail(string name, string detail, string nextAction) =>
        new(name, "fail", detail, nextAction);
}
