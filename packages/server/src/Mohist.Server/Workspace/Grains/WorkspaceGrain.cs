using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workspace;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
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
    private readonly IEventStore _eventStore;
    private readonly IGrainFactory _grainFactory;
    private readonly IBackgroundTaskLauncher _backgroundTasks;
    private readonly ILogger<WorkspaceGrain> _log;
    private WorkspaceState? _state;

    private const string SpecVersion = "1.0";

    public WorkspaceGrain(
        IDbContextFactory<MohistDbContext> dbFactory,
        IWorkspaceStore store,
        WorkspaceQuerier querier,
        ProjectQuerier projects,
        IEventStore eventStore,
        IGrainFactory grainFactory,
        IBackgroundTaskLauncher backgroundTasks,
        ILogger<WorkspaceGrain> log)
    {
        _dbFactory = dbFactory;
        _store = store;
        _querier = querier;
        _projects = projects;
        _eventStore = eventStore;
        _grainFactory = grainFactory;
        _backgroundTasks = backgroundTasks;
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
        => await CreateAsync(name, new WorkspaceOrigin.Manual(), repositoryNames ?? [], now);

    public async Task<WorkspaceState> CreateAsync(
        string name,
        WorkspaceOrigin origin,
        IReadOnlyList<string> repositoryNames,
        DateTimeOffset now)
    {
        var key = WorkspaceGrainKey.Parse(GrainKey);
        if (_state is not null)
            throw new WorkspaceDomainException("workspace_name_taken", $"Workspace '{_state.Name}' already exists in this project.");

        var project = await _projects.GetByIdAsync(key.ProjectId);
        if (project is null)
            throw new WorkspaceDomainException("workspace_project_not_found", "Project not found.");

        var error = WorkspacePolicy.ValidateCreate(
            name,
            origin,
            repositoryNames ?? [],
            project.Repositories.Select(r => r.Name).ToList());
        if (error is not null)
            throw new WorkspaceDomainException(error.Code, error.Message);

        var existing = await _store.FindActiveByOriginAsync(
            key.ProjectId,
            WorkspaceRowJson.OriginKind(origin),
            WorkspaceRowJson.OriginPayload(origin));
        if (existing is not null)
            throw new WorkspaceDomainException(
                "workspace_origin_conflict",
                $"Project already has an active workspace for this origin ('{existing.Name}'); close or archive it before creating another.");

        var state = new WorkspaceState
        {
            ProjectId = key.ProjectId,
            Name = name.Trim(),
            Origin = origin,
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
        _log.LogInformation("Workspace {ProjectId}/{Name} created ({OriginKind})", state.ProjectId, state.Name, WorkspaceRowJson.OriginKind(origin));
        await EmitCreatedAsync(state);
        return state;
    }

    public async Task<WorkspaceState> EnsureIssueWorkspaceAsync(int issueNumber, string repositoryName, DateTimeOffset now)
    {
        var key = WorkspaceGrainKey.Parse(GrainKey);
        var workspaceName = $"issue-{issueNumber}";
        if (!string.Equals(key.Name, workspaceName, StringComparison.Ordinal))
            throw new WorkspaceDomainException(
                "workspace_name_mismatch",
                $"Grain key name '{key.Name}' does not match expected name '{workspaceName}'.");

        if (_state is not null)
        {
            if (_state.Origin is WorkspaceOrigin.Issue org && org.IssueNumber == issueNumber)
                return _state;
            throw new WorkspaceDomainException(
                "workspace_name_taken",
                $"Workspace '{_state.Name}' already exists with a different origin.");
        }

        var project = await _projects.GetByIdAsync(key.ProjectId);
        if (project is null)
            throw new WorkspaceDomainException("workspace_project_not_found", "Project not found.");

        var origin = new WorkspaceOrigin.Issue(issueNumber);
        var existing = await _store.FindActiveByOriginAsync(
            key.ProjectId,
            WorkspaceRowJson.OriginKind(origin),
            WorkspaceRowJson.OriginPayload(origin));
        if (existing is not null)
        {
            _state = existing;
            return existing;
        }

        var state = new WorkspaceState
        {
            ProjectId = key.ProjectId,
            Name = workspaceName,
            Origin = origin,
            RepositoryNames = [repositoryName.Trim()],
            Status = WorkspaceStatus.Active,
            CreatedAt = now,
        };

        try
        {
            await _store.InsertAsync(state);
        }
        catch (DbUpdateException)
        {
            throw new WorkspaceDomainException(
                "workspace_conflict",
                $"Workspace '{workspaceName}' already exists in this project.",
                hint: "A workspace for this issue may have been created by a concurrent request.");
        }

        _state = state;
        _log.LogInformation("Workspace {ProjectId}/{Name} created (issue #{IssueNumber})", state.ProjectId, state.Name, issueNumber);
        await EmitCreatedAsync(state);
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

    public Task ArchiveByIssueAsync(int issueNumber, DateTimeOffset now)
        => ArchiveByOriginAsync(new WorkspaceOrigin.Issue(issueNumber), now);

    public async Task ArchiveByOriginAsync(WorkspaceOrigin origin, DateTimeOffset now)
    {
        var state = _state;
        if (state is null || state.Status == WorkspaceStatus.Archived) return;

        if (!Equals(state.Origin, origin))
            throw new WorkspaceDomainException(
                "workspace_origin_mismatch",
                $"Workspace '{state.Name}' does not belong to origin '{WorkspaceRowJson.OriginKind(origin)}'.");

        state.Status = WorkspaceStatus.Archived;
        state.ArchivedAt = now;
        await _store.SaveAsync(state);
        _log.LogInformation("Workspace {ProjectId}/{Name} archived ({OriginKind})", state.ProjectId, state.Name, WorkspaceRowJson.OriginKind(origin));
        await EmitArchivedAsync(state);
    }

    public async Task<WorkspaceState?> CloseAsync(DateTimeOffset now)
    {
        var state = _state;
        if (state is null) return null;

        if (state.Origin is WorkspaceOrigin.Issue)
            throw new WorkspaceDomainException(
                "workspace_close_not_allowed_for_issue",
                "Issue-backed workspaces are archived by the issue lifecycle, not by manual close.",
                hint: "Finish or close the issue instead ('mo issue done <number>' / 'mo issue close <number>').");

        if (state.Status == WorkspaceStatus.Archived)
            throw new WorkspaceDomainException(
                "workspace_already_archived",
                $"Workspace '{state.Name}' is already archived.");

        await EnsureNoActiveSessionsAsync(state);

        state.Status = WorkspaceStatus.Archived;
        state.ArchivedAt = now;
        await _store.SaveAsync(state);
        await EmitArchivedAsync(state);
        return state;
    }

    public Task<WorkspaceHome?> GetHomeAsync() =>
        Task.FromResult(WorkspacePolicy.ActiveHome(_state));

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
        var error = WorkspacePolicy.ValidateActiveSessions(state.Name, active);
        if (error is not null)
            throw new WorkspaceDomainException(
                error.Code,
                error.Message,
                hint: "Stop or wait for the bound sessions to finish, then retry. List them with 'mo session list --workspace <name>'.");
    }

    private async Task EmitCreatedAsync(WorkspaceState state)
    {
        var envelope = BuildEvent(state, EventCatalog.ReverseDns.WorkspaceCreated);
        await _eventStore.AppendAsync(envelope);
        PokeDispatcherBestEffort();
    }

    private async Task EmitArchivedAsync(WorkspaceState state)
    {
        var envelope = BuildEvent(state, EventCatalog.ReverseDns.WorkspaceArchived);
        await _eventStore.AppendAsync(envelope);
        PokeDispatcherBestEffort();
    }

    private static CloudEvent BuildEvent(WorkspaceState state, string type)
    {
        var source = WorkspaceEventPersistence.WorkspaceSource(state.ProjectId, state.Name);
        var extensions = WorkspaceLineage.BuildExtensions(state);
        var data = JsonSerializer.SerializeToElement(new
        {
            name = state.Name,
            originKind = WorkspaceRowJson.OriginKind(state.Origin),
            origin = state.Origin switch
            {
                WorkspaceOrigin.Issue issue => (object)new { issueNumber = issue.IssueNumber },
                WorkspaceOrigin.Manual => new { },
                WorkspaceOrigin.Slack slack => new { teamId = slack.TeamId, channelId = slack.ChannelId },
                WorkspaceOrigin.Web web => new { conversationId = web.ConversationId },
                WorkspaceOrigin.Cli => new { },
                _ => new { },
            },
            repositoryNames = state.RepositoryNames,
            status = state.Status == WorkspaceStatus.Active ? "active" : "archived",
            createdAt = state.CreatedAt,
            archivedAt = state.ArchivedAt,
        }, JSON.Options);

        return new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: DateTimeOffset.UtcNow,
            data: data,
            subject: state.Name,
            specVersion: SpecVersion,
            extensions: extensions);
    }

    private void PokeDispatcherBestEffort() =>
        EventDispatcherPoke.PokeAfterCommit(_grainFactory, _log, nameof(WorkspaceGrain), _backgroundTasks);
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
