using Microsoft.EntityFrameworkCore;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
using System.Text.Json;
using RepositoryPolicy = Mohist.Server.Project.Domain.RepositoryPolicy;

namespace Mohist.Server.Project.Grains;

public class ProjectGrain : Grain, IProjectGrain
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProjectGrain> _log;
    private ProjectInfo? _project;

    public ProjectGrain(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider,
        ILogger<ProjectGrain> log)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FindAsync(GrainKey);
        if (entry is not null)
            _project = ProjectQuerier.ToInfo(entry);
    }

    public Task<ProjectInfo?> GetAsync() => Task.FromResult(_project);

    public async Task<ProjectInfo> CreateAsync(string name, RepositoryInfo initialRepository)
    {
        if (_project is not null)
            throw new InvalidOperationException($"Project '{GrainKey}' already exists");

        name = ProjectName.NormalizeOrThrow(name);

        if (initialRepository is null)
            throw new ArgumentNullException(nameof(initialRepository));

        if (string.IsNullOrWhiteSpace(initialRepository.Name))
            throw new ArgumentException("Repository name is required.", nameof(initialRepository));
        if (string.IsNullOrWhiteSpace(initialRepository.GitUrl))
            throw new ArgumentException("gitUrl is required.", nameof(initialRepository));

        var initial = RepositoryPolicy.CreateInitial(
            initialRepository.Name,
            initialRepository.GitUrl,
            string.IsNullOrWhiteSpace(initialRepository.BaseBranch) ? null : initialRepository.BaseBranch);

        var validation = RepositoryPolicy.Validate([initial]);
        if (validation.Count > 0)
            throw new ArgumentException(string.Join("; ", validation.Select(v => v.Message)));

        var now = Now();
        var nowText = now.UtcDateTime.ToString("o");
        var serialized = JSON.Serialize(new List<RepositoryInfo>
        {
            new()
            {
                Name = initial.Name,
                GitUrl = initial.GitUrl,
                BaseBranch = initial.BaseBranch,
                IsDefault = initial.IsDefault,
            },
        });

        var entry = new ProjectRow
        {
            Id = GrainKey,
            Name = name,
            RepositoriesJson = serialized,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Projects.Add(entry);
        await db.SaveChangesAsync();

        _project = new ProjectInfo
        {
            Id = GrainKey,
            Name = entry.Name,
            CreatedAt = nowText,
            UpdatedAt = nowText,
            Repositories =
            [
                new RepositoryInfo
                {
                    Name = initial.Name,
                    GitUrl = initial.GitUrl,
                    BaseBranch = initial.BaseBranch,
                    IsDefault = initial.IsDefault,
                },
            ],
            Variables = ProjectVariablesBag.Empty,
        };

        _log.LogInformation("Project {Name} created", entry.Name);
        return _project;
    }

    public async Task<ProjectInfo?> UpdateAsync()
    {
        if (_project is null) return null;

        var now = Now();
        _project.UpdatedAt = now.UtcDateTime.ToString("o");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FindAsync(GrainKey);
        if (entry is null) return null;

        entry.UpdatedAt = now;
        await db.SaveChangesAsync();

        return _project;
    }

    public async Task DeleteAsync()
    {
        if (_project is null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FindAsync(GrainKey);
        if (entry is not null)
        {
            db.Projects.Remove(entry);
            await db.SaveChangesAsync();
        }

        _project = null;
    }

    public Task<List<RepositoryInfo>> ListRepositoriesAsync()
    {
        return Task.FromResult(_project?.Repositories ?? []);
    }

    public async Task<ProjectInfo?> AddRepositoryAsync(string repoName, string gitUrl, string? baseBranch, bool? setDefault = null)
    {
        if (_project is null) return null;

        var current = SnapshotNormalized(_project.Repositories);
        var build = RepositoryPolicy.BuildAdd(
            new RepositoryPolicy.TransitionInput(repoName, gitUrl, baseBranch, setDefault),
            current);

        if (!build.IsSuccess)
            throw new ArgumentException(string.Join("; ", build.Errors.Select(e => e.Message)));

        var added = build.Value;
        var next = ApplyAdd(current, added, setDefault ?? false);
        var validation = RepositoryPolicy.Validate(next);
        if (validation.Count > 0)
            throw new ArgumentException(string.Join("; ", validation.Select(v => v.Message)));

        _project.Repositories = next
            .Select(r => new RepositoryInfo
            {
                Name = r.Name,
                GitUrl = r.GitUrl,
                BaseBranch = r.BaseBranch,
                IsDefault = r.IsDefault,
            })
            .ToList();
        _project.UpdatedAt = Now().UtcDateTime.ToString("o");
        await PersistRepositoriesAsync();
        return _project;
    }

    public async Task<ProjectInfo?> UpdateRepositoryAsync(string repoName, string? gitUrl = null, string? baseBranch = null)
    {
        if (_project is null) return null;

        var current = SnapshotNormalized(_project.Repositories);
        var build = RepositoryPolicy.BuildUpdate(
            repoName,
            new RepositoryPolicy.TransitionInput(repoName, gitUrl, baseBranch),
            current);

        if (!build.IsSuccess)
        {
            if (build.Errors.Any(e => e.Code == "name"))
            {
                var nameError = build.Errors.First(e => e.Code == "name").Message;
                if (nameError.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            throw new ArgumentException(string.Join("; ", build.Errors.Select(e => e.Message)));
        }

        var update = build.Value;
        if (!update.Changed)
            return _project;

        var next = current
            .Select(r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase) ? update.Next : r)
            .ToList();
        var validation = RepositoryPolicy.Validate(next);
        if (validation.Count > 0)
            throw new ArgumentException(string.Join("; ", validation.Select(v => v.Message)));

        _project.Repositories = next
            .Select(r => new RepositoryInfo
            {
                Name = r.Name,
                GitUrl = r.GitUrl,
                BaseBranch = r.BaseBranch,
                IsDefault = r.IsDefault,
            })
            .ToList();
        _project.UpdatedAt = Now().UtcDateTime.ToString("o");
        await PersistRepositoriesAsync();
        return _project;
    }

    public async Task<ProjectInfo?> RemoveRepositoryAsync(string repoName)
    {
        if (_project is null) return null;

        var current = SnapshotNormalized(_project.Repositories);
        var build = RepositoryPolicy.BuildRemove(repoName, current);

        if (!build.IsSuccess)
        {
            if (build.Errors.Any(e => e.Code == "repository_default_deletion_conflict"))
                throw new InvalidOperationException(build.Errors.First().Message);
            if (build.Errors.Any(e => e.Code == "name" && e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
                return null;
            throw new ArgumentException(string.Join("; ", build.Errors.Select(e => e.Message)));
        }

        var next = current
            .Where(r => !string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var validation = RepositoryPolicy.Validate(next);
        if (validation.Count > 0)
            throw new ArgumentException(string.Join("; ", validation.Select(v => v.Message)));

        _project.Repositories = next
            .Select(r => new RepositoryInfo
            {
                Name = r.Name,
                GitUrl = r.GitUrl,
                BaseBranch = r.BaseBranch,
                IsDefault = r.IsDefault,
            })
            .ToList();
        _project.UpdatedAt = Now().UtcDateTime.ToString("o");
        await PersistRepositoriesAsync();
        return _project;
    }

    public async Task<ProjectInfo?> SetDefaultRepositoryAsync(string repoName)
    {
        if (_project is null) return null;

        var current = SnapshotNormalized(_project.Repositories);
        var build = RepositoryPolicy.BuildSetDefault(repoName, current);

        if (!build.IsSuccess)
        {
            if (build.Errors.Any(e => e.Code == "name"))
            {
                var nameError = build.Errors.First(e => e.Code == "name").Message;
                if (nameError.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            throw new ArgumentException(string.Join("; ", build.Errors.Select(e => e.Message)));
        }

        var (_, next) = build.Value;
        var previousDefault = current.Single(r => r.IsDefault);
        if (string.Equals(previousDefault.Name, next.Name, StringComparison.OrdinalIgnoreCase)
            && current.Count(r => r.IsDefault) == 1)
            return _project;

        var updated = current
            .Select(r => r with { IsDefault = false })
            .Select(r => string.Equals(r.Name, next.Name, StringComparison.OrdinalIgnoreCase) ? next : r)
            .ToList();

        var validation = RepositoryPolicy.Validate(updated);
        if (validation.Count > 0)
            throw new ArgumentException(string.Join("; ", validation.Select(v => v.Message)));

        _project.Repositories = updated
            .Select(r => new RepositoryInfo
            {
                Name = r.Name,
                GitUrl = r.GitUrl,
                BaseBranch = r.BaseBranch,
                IsDefault = r.IsDefault,
            })
            .ToList();
        _project.UpdatedAt = Now().UtcDateTime.ToString("o");
        await PersistRepositoriesAsync();
        return _project;
    }

    private async Task PersistRepositoriesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FindAsync(GrainKey);
        if (entry is null) return;

        entry.RepositoriesJson = JSON.Serialize(_project!.Repositories);
        entry.UpdatedAt = Now();
        await db.SaveChangesAsync();
    }

    public Task<ProjectVariablesBag?> GetVariablesAsync()
    {
        return Task.FromResult(_project?.Variables);
    }

    public async Task<ProjectVariablesBag?> PatchVariableAsync(string name, JsonElement value)
    {
        if (_project is null) return null;
        _project.Variables = _project.Variables.PatchVar(name, value);
        await PersistVariablesAsync();
        return _project.Variables;
    }

    public async Task<ProjectVariablesBag?> DeleteVariableAsync(string name)
    {
        if (_project is null) return null;
        _project.Variables = _project.Variables.DeleteVar(name);
        await PersistVariablesAsync();
        return _project.Variables;
    }

    public async Task<ProjectVariablesBag?> PatchStageVariableAsync(string stage, string name, JsonElement value)
    {
        if (_project is null) return null;
        _project.Variables = _project.Variables.PatchStageVar(stage, name, value);
        await PersistVariablesAsync();
        return _project.Variables;
    }

    public async Task<ProjectVariablesBag?> DeleteStageVariableAsync(string stage, string name)
    {
        if (_project is null) return null;
        _project.Variables = _project.Variables.DeleteStageVar(stage, name);
        await PersistVariablesAsync();
        return _project.Variables;
    }

    private async Task PersistVariablesAsync()
    {
        var now = Now();
        _project!.UpdatedAt = now.UtcDateTime.ToString("o");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.Projects.FindAsync(GrainKey);
        if (entry is null) return;

        var bundle = new VariableBundle(
            ToJsonObject(_project.Variables.Vars),
            _project.Variables.Stages?.ToDictionary(
                kv => kv.Key,
                kv => new StageVariables(ToJsonObject(kv.Value?.Vars)),
                StringComparer.OrdinalIgnoreCase));
        await UpsertWorkflowProfileVariablesAsync(db, GrainKey, bundle, now);
        entry.UpdatedAt = now;
        await db.SaveChangesAsync();
    }

    private static async Task UpsertWorkflowProfileVariablesAsync(
        MohistDbContext db,
        string projectId,
        VariableBundle bundle,
        DateTimeOffset now)
    {
        var row = await db.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);

        if (row is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                Variables = bundle.ToJson(),
                UpdatedAt = now,
            });
            return;
        }

        row.Variables = bundle.ToJson();
        row.UpdatedAt = now;
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow();

    private static JsonElement? ToJsonObject(Dictionary<string, JsonElement?>? values)
    {
        if (values is null || values.Count == 0)
            return null;

        return JSON.SerializeToElement(values);
    }

    private static IReadOnlyList<RepositoryPolicy.NormalizedRepository> SnapshotNormalized(
        IEnumerable<RepositoryInfo> repositories) =>
        repositories
            .Select(r => new RepositoryPolicy.NormalizedRepository(
                r.Name,
                r.GitUrl,
                r.BaseBranch,
                r.IsDefault))
            .ToList();

    private static List<RepositoryPolicy.NormalizedRepository> ApplyAdd(
        IReadOnlyList<RepositoryPolicy.NormalizedRepository> current,
        RepositoryPolicy.NormalizedRepository added,
        bool setDefault)
    {
        var next = current.ToList();
        next.Add(added);

        if (setDefault)
        {
            for (var i = 0; i < next.Count; i++)
                next[i] = next[i] with { IsDefault = false };
            next[^1] = next[^1] with { IsDefault = true };
        }
        else if (added.IsDefault)
        {
            for (var i = 0; i < next.Count - 1; i++)
                next[i] = next[i] with { IsDefault = false };
        }

        return next;
    }

}
