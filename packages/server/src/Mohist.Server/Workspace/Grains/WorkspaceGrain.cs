using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workspace;
using Mohist.Server.Project.Services;
using Mohist.Server.Workspace.Domain;
using Mohist.Server.Workspace.Services;

namespace Mohist.Server.Workspace.Grains;

public sealed class WorkspaceGrain : Grain, IWorkspaceGrain
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IWorkspaceStore _store;
    private readonly WorkspaceQuerier _querier;
    private readonly ProjectQuerier _projects;
    private readonly ILogger<WorkspaceGrain> _log;
    private WorkspaceState? _state;

    public WorkspaceGrain(
        IDbContextFactory<MohistDbContext> dbFactory,
        IWorkspaceStore store,
        WorkspaceQuerier querier,
        ProjectQuerier projects,
        ILogger<WorkspaceGrain> log)
    {
        _dbFactory = dbFactory;
        _store = store;
        _querier = querier;
        _projects = projects;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        var key = WorkspaceGrainKey.Parse(GrainKey);
        _state = await _store.FindAsync(key.ProjectId, key.Name, ct);
    }

    public Task<WorkspaceState?> GetAsync() => Task.FromResult(_state);

    public async Task<WorkspaceState> CreateManualAsync(string name, string[] repositoryNames, DateTimeOffset now)
    {
        var key = WorkspaceGrainKey.Parse(GrainKey);
        if (_state is not null)
            throw new WorkspaceDomainException("workspace_name_taken", $"Workspace '{_state.Name}' already exists in this project.");

        var project = await _projects.GetByIdAsync(key.ProjectId);
        if (project is null)
            throw new WorkspaceDomainException("workspace_project_not_found", "Project not found.");

        var error = WorkspacePolicy.ValidateCreate(
            name,
            new WorkspaceOrigin.Manual(),
            repositoryNames ?? [],
            project.Repositories.Select(r => r.Name).ToList());
        if (error is not null)
            throw new WorkspaceDomainException(error.Code, error.Message);

        var existing = await _store.FindActiveByOriginAsync(
            key.ProjectId,
            WorkspaceRowJson.OriginKind(new WorkspaceOrigin.Manual()),
            WorkspaceRowJson.OriginPayload(new WorkspaceOrigin.Manual()));
        if (existing is not null)
            throw new WorkspaceDomainException(
                "workspace_origin_conflict",
                $"Project already has an active manual workspace '{existing.Name}'; close it before creating another.");

        var state = new WorkspaceState
        {
            ProjectId = key.ProjectId,
            Name = name.Trim(),
            Origin = new WorkspaceOrigin.Manual(),
            RepositoryNames = (repositoryNames ?? [])
                .Select(r => r.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Status = WorkspaceStatus.Active,
            CreatedAt = now,
        };

        try
        {
            await _store.InsertAsync(state);
        }
        catch (DbUpdateException)
        {
            // The DB unique constraints (name, active origin) are the hard
            // backstop behind the grain pre-checks; a race surfaces here.
            throw new WorkspaceDomainException(
                "workspace_conflict",
                "Workspace name or origin conflicts with an existing workspace.",
                hint: "List workspaces with 'mo workspace list' to see existing names.");
        }

        _state = state;
        _log.LogInformation("Workspace {ProjectId}/{Name} created (manual)", state.ProjectId, state.Name);
        return state;
    }

    public async Task<WorkspaceState?> AddRepositoryAsync(string repoName)
    {
        var state = RequireActive();
        if (state is null) return null;

        var project = await _projects.GetByIdAsync(state.ProjectId);
        if (project is null)
            throw new WorkspaceDomainException("workspace_project_not_found", "Project not found.");

        var error = WorkspacePolicy.ValidateAddRepository(
            repoName,
            state.RepositoryNames,
            project.Repositories.Select(r => r.Name).ToList());
        if (error is not null)
            throw new WorkspaceDomainException(error.Code, error.Message);

        await EnsureNoActiveSessionsAsync(state);

        state.RepositoryNames.Add(repoName.Trim());
        await _store.SaveAsync(state);
        return state;
    }

    public async Task<WorkspaceState?> RemoveRepositoryAsync(string repoName)
    {
        var state = RequireActive();
        if (state is null) return null;

        var error = WorkspacePolicy.ValidateRemoveRepository(repoName, state.RepositoryNames);
        if (error is not null)
            throw new WorkspaceDomainException(error.Code, error.Message);

        await EnsureNoActiveSessionsAsync(state);

        state.RepositoryNames.RemoveAll(r => string.Equals(r, repoName.Trim(), StringComparison.OrdinalIgnoreCase));
        await _store.SaveAsync(state);
        return state;
    }

    public async Task<WorkspaceState?> CloseAsync(DateTimeOffset now)
    {
        var state = _state;
        if (state is null) return null;

        if (state.Origin is WorkspaceOrigin.Issue)
            throw new WorkspaceDomainException(
                "workspace_close_not_allowed_for_issue",
                "Issue-backed workspaces are archived by the issue lifecycle, not by manual close.",
                hint: "Finish or cancel the issue instead ('mo issue done <number>' / 'mo issue cancel <number>').");

        if (state.Status == WorkspaceStatus.Archived)
            throw new WorkspaceDomainException(
                "workspace_already_archived",
                $"Workspace '{state.Name}' is already archived.");

        await EnsureNoActiveSessionsAsync(state);

        state.Status = WorkspaceStatus.Archived;
        state.ArchivedAt = now;
        await _store.SaveAsync(state);
        return state;
    }

    public Task<WorkspaceHome?> GetHomeAsync() =>
        Task.FromResult(_state?.Status == WorkspaceStatus.Active ? _state.Home : null);

    public async Task<WorkspaceHome?> EnsureMaterializedOnAsync(string runnerId, string path, DateTimeOffset now)
    {
        var state = _state;
        if (state is null) return null;

        if (state.Status != WorkspaceStatus.Active)
            throw new WorkspaceDomainException("workspace_archived", $"Workspace '{state.Name}' is archived and cannot be materialized.");

        if (state.Home is not null && !string.Equals(state.Home.RunnerId, runnerId, StringComparison.Ordinal))
            throw new WorkspaceDomainException(
                "workspace_home_claimed",
                $"Workspace '{state.Name}' is already materialized on runner '{state.Home.RunnerId}'.",
                hint: "The dispatching runner must yield its local directory; the job retries against the home runner.");

        if (state.Home is not null
            && string.Equals(state.Home.RunnerId, runnerId, StringComparison.Ordinal)
            && string.Equals(state.Home.Path, path, StringComparison.Ordinal))
        {
            return state.Home;
        }

        state.Home = new WorkspaceHome(runnerId, path);
        await _store.SaveAsync(state);
        return state.Home;
    }

    public async Task ClearHomeIfAsync(string runnerId)
    {
        var state = _state;
        if (state is null || state.Home is null) return;
        if (!string.Equals(state.Home.RunnerId, runnerId, StringComparison.Ordinal)) return;
        state.Home = null;
        await _store.SaveAsync(state);
    }

    private WorkspaceState? RequireActive()
    {
        var state = _state;
        if (state is null) return null;
        if (state.Status != WorkspaceStatus.Active)
            throw new WorkspaceDomainException("workspace_archived", $"Workspace '{state.Name}' is archived.");
        return state;
    }

    private async Task EnsureNoActiveSessionsAsync(WorkspaceState state)
    {
        var active = await _querier.CountActiveBoundSessionsAsync(state.ProjectId, state.Name);
        if (active > 0)
            throw new WorkspaceDomainException(
                "workspace_has_active_sessions",
                $"Workspace '{state.Name}' has {active} active bound session(s).",
                hint: "Stop or wait for the bound sessions to finish, then retry. List them with 'mo session list --workspace <name>'.");
    }
}

internal readonly record struct WorkspaceGrainKey(string ProjectId, string Name)
{
    public static WorkspaceGrainKey Parse(string grainKey)
    {
        var separatorIndex = grainKey.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == grainKey.Length - 1)
            throw new InvalidOperationException($"Invalid workspace grain key '{grainKey}'.");
        return new WorkspaceGrainKey(grainKey[..separatorIndex], grainKey[(separatorIndex + 1)..]);
    }
}
