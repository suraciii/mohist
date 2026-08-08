using Mohist.Server.Infrastructure.Data.Workspace;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Workspace.Domain;
using Mohist.Server.Workspace.Grains;
using Mohist.Server.Workspace.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workspace;

public sealed class InteractionWorkspaceProvisionerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnsureSlackWorkspace_FirstTrigger_CreatesActiveWorkspaceWithSlackOrigin()
    {
        var store = new FakeWorkspaceStore();
        var grains = new FakeGrainFactory(store);
        var provisioner = new InteractionWorkspaceProvisioner(store, grains);

        var name = await provisioner.EnsureSlackWorkspaceAsync("p1", "T1", "C1", Now);

        Assert.Equal("slack-C1", name);
        var ws = Assert.Single(store.All);
        Assert.Equal("p1", ws.ProjectId);
        Assert.Equal(new WorkspaceOrigin.Slack("T1", "C1"), ws.Origin);
        Assert.Equal(WorkspaceStatus.Active, ws.Status);
        Assert.Empty(ws.RepositoryNames);
    }

    [Fact]
    public async Task EnsureSlackWorkspace_ActiveWorkspaceExists_ReusesWithoutCreating()
    {
        var store = new FakeWorkspaceStore();
        store.All.Add(new WorkspaceState
        {
            ProjectId = "p1",
            Name = "slack-C1",
            Origin = new WorkspaceOrigin.Slack("T1", "C1"),
            Status = WorkspaceStatus.Active,
        });
        var grains = new FakeGrainFactory(store);
        var provisioner = new InteractionWorkspaceProvisioner(store, grains);

        var name = await provisioner.EnsureSlackWorkspaceAsync("p1", "T1", "C1", Now);

        Assert.Equal("slack-C1", name);
        Assert.Empty(grains.Created);
    }

    [Fact]
    public async Task EnsureSlackWorkspace_ArchivedRowHoldsBaseName_DerivesSuffixedName()
    {
        var store = new FakeWorkspaceStore();
        store.All.Add(new WorkspaceState
        {
            ProjectId = "p1",
            Name = "slack-C1",
            Origin = new WorkspaceOrigin.Slack("T1", "C1"),
            Status = WorkspaceStatus.Archived,
        });
        var grains = new FakeGrainFactory(store);
        var provisioner = new InteractionWorkspaceProvisioner(store, grains);

        var name = await provisioner.EnsureSlackWorkspaceAsync("p1", "T1", "C1", Now);

        Assert.Equal("slack-C1-2", name);
        var created = Assert.Single(grains.Created);
        Assert.Equal(new WorkspaceOrigin.Slack("T1", "C1"), created.Origin);
    }

    [Fact]
    public async Task EnsureSlackWorkspace_BaseAndFirstSuffixTaken_DerivesSecondSuffix()
    {
        var store = new FakeWorkspaceStore();
        store.All.Add(new WorkspaceState
        {
            ProjectId = "p1",
            Name = "slack-C1",
            Origin = new WorkspaceOrigin.Slack("T1", "C1"),
            Status = WorkspaceStatus.Archived,
        });
        store.All.Add(new WorkspaceState
        {
            ProjectId = "p1",
            Name = "slack-C1-2",
            Origin = new WorkspaceOrigin.Manual(),
            Status = WorkspaceStatus.Archived,
        });
        var provisioner = new InteractionWorkspaceProvisioner(store, new FakeGrainFactory(store));

        var name = await provisioner.EnsureSlackWorkspaceAsync("p1", "T1", "C1", Now);

        Assert.Equal("slack-C1-3", name);
    }

    [Fact]
    public async Task EnsureSlackWorkspace_ConcurrentWinner_ReturnsWinnerName()
    {
        var store = new FakeWorkspaceStore();
        var grains = new FakeGrainFactory(store);
        var provisioner = new InteractionWorkspaceProvisioner(store, grains);

        // Simulate the loser: its create attempt collides (name already
        // taken by the winner) before it re-queries the active origin.
        var winnerName = await provisioner.EnsureSlackWorkspaceAsync("p1", "T1", "C1", Now);
        Assert.Equal("slack-C1", winnerName);

        // Second ensure is the "conflict replay" path: active origin hit.
        var replayed = await provisioner.EnsureSlackWorkspaceAsync("p1", "T1", "C1", Now);
        Assert.Equal("slack-C1", replayed);
        Assert.Single(store.All);
    }

    [Fact]
    public async Task EnsureWebWorkspace_CreatesWebOriginWorkspace()
    {
        var store = new FakeWorkspaceStore();
        var provisioner = new InteractionWorkspaceProvisioner(store, new FakeGrainFactory(store));

        var name = await provisioner.EnsureWebWorkspaceAsync("p1", "conv-1", Now);

        Assert.Equal("web-conv-1", name);
        var ws = Assert.Single(store.All);
        Assert.Equal(new WorkspaceOrigin.Web("conv-1"), ws.Origin);
    }

    [Fact]
    public async Task EnsureCliWorkspace_CreatesDeterministicCurrentProjectWorkspace()
    {
        var store = new FakeWorkspaceStore();
        var grains = new FakeGrainFactory(store);
        var provisioner = new InteractionWorkspaceProvisioner(store, grains);

        var name = await provisioner.EnsureCliWorkspaceAsync("p1", Now);

        Assert.Equal("cli-current", name);
        var ws = Assert.Single(store.All);
        Assert.Equal(new WorkspaceOrigin.Cli(), ws.Origin);
        Assert.Equal("cli", WorkspaceRowJson.OriginKind(ws.Origin));
    }

    [Fact]
    public async Task EnsureCliWorkspace_ReusesActiveWorkspaceAcrossLaunches()
    {
        var store = new FakeWorkspaceStore();
        store.All.Add(new WorkspaceState
        {
            ProjectId = "p1",
            Name = "cli-current",
            Origin = new WorkspaceOrigin.Cli(),
            Status = WorkspaceStatus.Active,
        });
        var grains = new FakeGrainFactory(store);
        var provisioner = new InteractionWorkspaceProvisioner(store, grains);

        var name = await provisioner.EnsureCliWorkspaceAsync("p1", Now);

        Assert.Equal("cli-current", name);
        Assert.Empty(grains.Created);
    }

    [Fact]
    public async Task ArchiveSlackChannel_NoActiveWorkspace_ReturnsFalse()
    {
        var store = new FakeWorkspaceStore();
        var grains = new FakeGrainFactory(store);
        var provisioner = new InteractionWorkspaceProvisioner(store, grains);

        var archived = await provisioner.ArchiveSlackChannelAsync("p1", "T1", "C1", Now);

        Assert.False(archived);
        Assert.Empty(grains.Archived);
    }

    [Fact]
    public async Task ArchiveSlackChannel_ActiveWorkspace_ArchivesItAndReturnsTrue()
    {
        var store = new FakeWorkspaceStore();
        store.All.Add(new WorkspaceState
        {
            ProjectId = "p1",
            Name = "slack-C1",
            Origin = new WorkspaceOrigin.Slack("T1", "C1"),
            Status = WorkspaceStatus.Active,
        });
        var grains = new FakeGrainFactory(store);
        var provisioner = new InteractionWorkspaceProvisioner(store, grains);

        var archived = await provisioner.ArchiveSlackChannelAsync("p1", "T1", "C1", Now);

        Assert.True(archived);
        var grain = Assert.Single(grains.Archived);
        Assert.Equal("slack-C1", grain.Name);
        Assert.Equal(new WorkspaceOrigin.Slack("T1", "C1"), grain.Origin);
        Assert.Equal(WorkspaceStatus.Archived, Assert.Single(store.All).Status);
    }

    [Fact]
    public async Task ArchiveSlackChannel_Twice_SecondCallIsIdempotent()
    {
        var store = new FakeWorkspaceStore();
        store.All.Add(new WorkspaceState
        {
            ProjectId = "p1",
            Name = "slack-C1",
            Origin = new WorkspaceOrigin.Slack("T1", "C1"),
            Status = WorkspaceStatus.Active,
        });
        var grains = new FakeGrainFactory(store);
        var provisioner = new InteractionWorkspaceProvisioner(store, grains);

        Assert.True(await provisioner.ArchiveSlackChannelAsync("p1", "T1", "C1", Now));
        Assert.False(await provisioner.ArchiveSlackChannelAsync("p1", "T1", "C1", Now));
        Assert.Single(grains.Archived);
    }

    [Fact]
    public async Task ArchiveWebConversation_ActiveWorkspace_ArchivesIt()
    {
        var store = new FakeWorkspaceStore();
        store.All.Add(new WorkspaceState
        {
            ProjectId = "p1",
            Name = "web-conv-1",
            Origin = new WorkspaceOrigin.Web("conv-1"),
            Status = WorkspaceStatus.Active,
        });
        var provisioner = new InteractionWorkspaceProvisioner(store, new FakeGrainFactory(store));

        var archived = await provisioner.ArchiveWebConversationAsync("p1", "conv-1", Now);

        Assert.True(archived);
        Assert.Equal(WorkspaceStatus.Archived, Assert.Single(store.All).Status);
    }

    private sealed class FakeWorkspaceStore : IWorkspaceStore
    {
        public List<WorkspaceState> All { get; } = [];

        public Task<WorkspaceState?> FindAsync(string projectId, string name, CancellationToken ct = default)
            => Task.FromResult(All.FirstOrDefault(ws => ws.ProjectId == projectId && ws.Name == name));

        public Task<WorkspaceState?> FindActiveByOriginAsync(string projectId, string originKind, string originPayloadJson, CancellationToken ct = default)
            => Task.FromResult(All.FirstOrDefault(ws =>
                ws.ProjectId == projectId
                && ws.Status == WorkspaceStatus.Active
                && WorkspaceRowJson.OriginKind(ws.Origin) == originKind
                && WorkspaceRowJson.OriginPayload(ws.Origin) == originPayloadJson));

        public Task<IReadOnlyList<WorkspaceState>> ListAsync(string projectId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkspaceState>>(All.Where(ws => ws.ProjectId == projectId).ToList());

        public Task InsertAsync(WorkspaceState state, CancellationToken ct = default)
        {
            if (All.Any(ws => ws.ProjectId == state.ProjectId && ws.Name == state.Name))
                throw new InvalidOperationException("duplicate name");
            if (All.Any(ws =>
                    ws.ProjectId == state.ProjectId
                    && ws.Status == WorkspaceStatus.Active
                    && WorkspaceRowJson.OriginKind(ws.Origin) == WorkspaceRowJson.OriginKind(state.Origin)
                    && WorkspaceRowJson.OriginPayload(ws.Origin) == WorkspaceRowJson.OriginPayload(state.Origin)))
            {
                throw new InvalidOperationException("duplicate active origin");
            }
            All.Add(state);
            return Task.CompletedTask;
        }

        public Task SaveAsync(WorkspaceState state, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeGrainFactory : IGrainFactory
    {
        private readonly FakeWorkspaceStore _store;

        public FakeGrainFactory(FakeWorkspaceStore store) => _store = store;

        public List<(string Name, WorkspaceOrigin Origin)> Created { get; } = [];
        public List<(string Name, WorkspaceOrigin Origin)> Archived { get; } = [];

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) != typeof(IWorkspaceGrain))
                throw new NotSupportedException($"{nameof(FakeGrainFactory)} does not support {typeof(TGrainInterface).Name}");
            var key = WorkspaceGrainKey.Parse(primaryKey);
            return (TGrainInterface)(object)new FakeWorkspaceGrain(key, _store, Created, Archived);
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
            => throw new NotSupportedException();
    }

    private sealed class FakeWorkspaceGrain(
        WorkspaceGrainKey key,
        FakeWorkspaceStore store,
        List<(string Name, WorkspaceOrigin Origin)> created,
        List<(string Name, WorkspaceOrigin Origin)> archived) : IWorkspaceGrain
    {
        public Task<WorkspaceState?> GetAsync() => store.FindAsync(key.ProjectId, key.Name);

        public Task<WorkspaceState> CreateManualAsync(string name, string[] repositoryNames, DateTimeOffset now)
            => throw new NotSupportedException();

        public async Task<WorkspaceState> CreateAsync(string name, WorkspaceOrigin origin, IReadOnlyList<string> repositoryNames, DateTimeOffset now)
        {
            if (await store.FindAsync(key.ProjectId, name) is not null)
                throw new WorkspaceDomainException("workspace_name_taken", "name taken");
            var active = await store.FindActiveByOriginAsync(
                key.ProjectId,
                WorkspaceRowJson.OriginKind(origin),
                WorkspaceRowJson.OriginPayload(origin));
            if (active is not null)
                throw new WorkspaceDomainException("workspace_origin_conflict", "origin conflict");
            var state = new WorkspaceState
            {
                ProjectId = key.ProjectId,
                Name = name,
                Origin = origin,
                Status = WorkspaceStatus.Active,
                CreatedAt = now,
            };
            try
            {
                await store.InsertAsync(state);
            }
            catch (InvalidOperationException)
            {
                throw new WorkspaceDomainException("workspace_conflict", "insert conflict");
            }
            created.Add((name, origin));
            return state;
        }

        public Task<WorkspaceState> EnsureIssueWorkspaceAsync(int issueNumber, string repositoryName, DateTimeOffset now)
            => throw new NotSupportedException();

        public Task<WorkspaceState?> AddRepositoryAsync(string repoName) => throw new NotSupportedException();

        public Task<WorkspaceState?> RemoveRepositoryAsync(string repoName) => throw new NotSupportedException();

        public async Task ArchiveByOriginAsync(WorkspaceOrigin origin, DateTimeOffset now)
        {
            var state = await store.FindAsync(key.ProjectId, key.Name);
            if (state is null || state.Status == WorkspaceStatus.Archived) return;
            if (!Equals(state.Origin, origin))
                throw new WorkspaceDomainException("workspace_origin_mismatch", "origin mismatch");
            state.Status = WorkspaceStatus.Archived;
            state.ArchivedAt = now;
            archived.Add((state.Name, origin));
        }

        public Task ArchiveByIssueAsync(int issueNumber, DateTimeOffset now) => throw new NotSupportedException();

        public Task<WorkspaceState?> CloseAsync(DateTimeOffset now) => throw new NotSupportedException();

        public Task<WorkspaceHome?> GetHomeAsync() => throw new NotSupportedException();

        public Task<WorkspaceHome?> EnsureMaterializedOnAsync(string runnerId, string path, DateTimeOffset now)
            => throw new NotSupportedException();

        public Task ClearHomeIfAsync(string runnerId) => throw new NotSupportedException();
    }

    private readonly record struct WorkspaceGrainKey(string ProjectId, string Name)
    {
        public static WorkspaceGrainKey Parse(string grainKey)
        {
            var separatorIndex = grainKey.IndexOf(':');
            return new WorkspaceGrainKey(grainKey[..separatorIndex], grainKey[(separatorIndex + 1)..]);
        }
    }
}
