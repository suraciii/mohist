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

public sealed record DoctorFactSnapshot(
    DoctorRevisionFacts Revision,
    bool MigrationsCurrent,
    IReadOnlyList<string> ProjectsMissingVerificationCommands,
    IReadOnlyList<string> IncompleteRuntimeCatalogs);

public interface IDoctorFactSource
{
    Task<DoctorRevisionFacts> GetRevisionFactsAsync(CancellationToken ct);
    Task<bool> AreMigrationsCurrentAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> GetProjectsMissingVerificationCommandsAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> GetIncompleteRuntimeCatalogsAsync(CancellationToken ct);
}

public sealed class DoctorFactSource : IDoctorFactSource, IScopedService
{
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

    public async Task<IReadOnlyList<string>> GetProjectsMissingVerificationCommandsAsync(CancellationToken ct)
    {
        var projects = await _projects.ListAllAsync();
        return projects
            .Where(project => string.IsNullOrWhiteSpace(project.VerificationCommand))
            .Select(project => project.Name)
            .ToArray();
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
    private static readonly string[] CanonicalNames =
    [
        "revision-alignment",
        "migrations",
        "verification-command",
        "model-catalog",
    ];

    private readonly IDoctorFactSource _facts;

    public DoctorCheckService(IDoctorFactSource facts)
    {
        _facts = facts;
    }

    public async Task<IReadOnlyList<DoctorCheck>> GetChecksAsync(CancellationToken ct = default)
    {
        var checks = new List<DoctorCheck>(CanonicalNames.Length)
        {
            await EvaluateAsync("revision-alignment", () => _facts.GetRevisionFactsAsync(ct), EvaluateRevision),
            await EvaluateAsync("migrations", () => _facts.AreMigrationsCurrentAsync(ct), EvaluateMigrations),
            await EvaluateAsync("verification-command", () => _facts.GetProjectsMissingVerificationCommandsAsync(ct), EvaluateVerification),
            await EvaluateAsync("model-catalog", () => _facts.GetIncompleteRuntimeCatalogsAsync(ct), EvaluateCatalog),
        };
        return checks;
    }

    public static IReadOnlyList<DoctorCheck> Evaluate(DoctorFactSnapshot facts) =>
    [
        EvaluateRevision(facts.Revision),
        EvaluateMigrations(facts.MigrationsCurrent),
        EvaluateVerification(facts.ProjectsMissingVerificationCommands),
        EvaluateCatalog(facts.IncompleteRuntimeCatalogs),
    ];

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
            ? new DoctorCheck("revision-alignment", "ok", "Known component revisions are aligned", null)
            : Fail("revision-alignment", $"Component revisions differ: {string.Join(", ", known.Select(pair => $"{pair.Key}={pair.Value}"))}", "Deploy the same revision to CLI, Server, Runner, and Slack, then run mo doctor again.");
    }

    private static DoctorCheck EvaluateMigrations(bool current) =>
        current
            ? new DoctorCheck("migrations", "ok", "Database schema is at the current migration boundary", null)
            : Fail("migrations", "Database has pending migrations", "Run the Server database migration before starting workflows.");

    private static DoctorCheck EvaluateVerification(IReadOnlyList<string> missing) =>
        missing.Count == 0
            ? new DoctorCheck("verification-command", "ok", "All Projects have a verification command", null)
            : Fail("verification-command", $"Projects missing verification commands: {string.Join(", ", missing)}", "Set a verification command for each listed Project with mo project set-verification-command.");

    private static DoctorCheck EvaluateCatalog(IReadOnlyList<string> incomplete) =>
        incomplete.Count == 0
            ? new DoctorCheck("model-catalog", "ok", "All discovered runtime catalogs are complete", null)
            : Fail("model-catalog", $"Runtime catalogs are empty or incomplete: {string.Join(", ", incomplete)}", "Reconnect or refresh the affected Runner runtime catalogs, then run mo doctor again.");

    private static DoctorCheck Fail(string name, string detail, string nextAction) =>
        new(name, "fail", detail, nextAction);
}
