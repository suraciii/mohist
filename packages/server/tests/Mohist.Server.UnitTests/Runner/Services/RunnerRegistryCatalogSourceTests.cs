using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Orleans;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.Services;

public class RunnerRegistryCatalogSourceTests
{
    [Fact]
    public async Task GetCatalogAsync_NoRunners_ReturnsNull()
    {
        var factory = new RegistryGrainFactory(new FakeRunnerRegistryGrain());

        var source = new RunnerRegistryCatalogSource(factory);

        Assert.Null(await source.GetCatalogAsync());
    }

    [Fact]
    public async Task GetCatalogAsync_RunnerWithoutCatalog_ReturnsNull()
    {
        var registeredAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var factory = new RegistryGrainFactory(new FakeRunnerRegistryGrain(
            CreateRunner("runner-1", registeredAt, catalog: null)));

        var source = new RunnerRegistryCatalogSource(factory);

        Assert.Null(await source.GetCatalogAsync());
    }

    [Fact]
    public async Task GetCatalogAsync_SingleRunnerWithCatalog_ReturnsItsCatalog()
    {
        var registeredAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var catalog = CreateCatalog("alpha");
        var factory = new RegistryGrainFactory(new FakeRunnerRegistryGrain(
            CreateRunner("runner-1", registeredAt, catalog)));

        var source = new RunnerRegistryCatalogSource(factory);

        var resolved = await source.GetCatalogAsync();

        Assert.NotNull(resolved);
        Assert.Equal(catalog.Actions[0].Name, resolved!.Actions[0].Name);
    }

    [Fact]
    public async Task GetCatalogAsync_MultipleRunners_LatestRegisteredWins()
    {
        var earlier = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        var earliest = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

        var catalogEarliest = CreateCatalog("earliest");
        var catalogEarlier = CreateCatalog("earlier");
        var catalogLater = CreateCatalog("later");

        var factory = new RegistryGrainFactory(new FakeRunnerRegistryGrain(
            CreateRunner("earliest", earliest, catalogEarliest),
            CreateRunner("earlier", earlier, catalogEarlier),
            CreateRunner("later", later, catalogLater)));

        var source = new RunnerRegistryCatalogSource(factory);

        var resolved = await source.GetCatalogAsync();

        Assert.NotNull(resolved);
        Assert.Equal("alpha/later", resolved!.Actions[0].Name);
    }

    [Fact]
    public async Task GetCatalogAsync_NullRegisteredAt_TreatedAsOldest()
    {
        var later = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        var withTimestamp = CreateCatalog("with-timestamp");
        var nullTimestamp = CreateCatalog("null-timestamp");

        var factory = new RegistryGrainFactory(new FakeRunnerRegistryGrain(
            CreateRunner("null-timestamp", null, nullTimestamp),
            CreateRunner("with-timestamp", later, withTimestamp)));

        var source = new RunnerRegistryCatalogSource(factory);

        var resolved = await source.GetCatalogAsync();

        Assert.NotNull(resolved);
        Assert.Equal("alpha/with-timestamp", resolved!.Actions[0].Name);
    }

    [Fact]
    public async Task GetCatalogAsync_AllNullRegisteredAt_PicksFirstNonNullCatalog()
    {
        var catalogA = CreateCatalog("a");
        var catalogB = CreateCatalog("b");

        var factory = new RegistryGrainFactory(new FakeRunnerRegistryGrain(
            CreateRunner("a", null, catalogA),
            CreateRunner("b", null, catalogB)));

        var source = new RunnerRegistryCatalogSource(factory);

        var resolved = await source.GetCatalogAsync();

        Assert.NotNull(resolved);
        Assert.Equal("alpha/a", resolved!.Actions[0].Name);
    }

    [Fact]
    public async Task GetCatalogAsync_EqualRegisteredAt_UsesRunnerIdTieBreak()
    {
        var registeredAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var factory = new RegistryGrainFactory(new FakeRunnerRegistryGrain(
            CreateRunner("runner-z", registeredAt, CreateCatalog("z")),
            CreateRunner("runner-a", registeredAt, CreateCatalog("a"))));

        var source = new RunnerRegistryCatalogSource(factory);

        var resolved = await source.GetCatalogAsync();

        Assert.NotNull(resolved);
        Assert.Equal("alpha/z", resolved!.Actions[0].Name);
    }

    [Fact]
    public async Task GetCatalogAsync_OnlyRegisteredWithoutCatalog_ReturnsNull()
    {
        var later = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        var factory = new RegistryGrainFactory(new FakeRunnerRegistryGrain(
            CreateRunner("no-catalog-1", later, null),
            CreateRunner("no-catalog-2", later.AddHours(1), null)));

        var source = new RunnerRegistryCatalogSource(factory);

        Assert.Null(await source.GetCatalogAsync());
    }

    private static RunnerInfo CreateRunner(string runnerId, DateTimeOffset? registeredAt, ActionCatalog? catalog)
        => new(
            RunnerId: runnerId,
            Capabilities: ["spec/*"],
            Hostname: "test-host",
            ProjectId: null,
            RegisteredAt: registeredAt,
            ActionCatalog: catalog);

    private static ActionCatalog CreateCatalog(string suffix) =>
        new(
            [new ActionCatalogEntry($"alpha/{suffix}", [], [], [])],
            []);

    private sealed class FakeRunnerRegistryGrain : IRunnerRegistryGrain
    {
        private readonly IReadOnlyList<RunnerInfo> _runners;

        public FakeRunnerRegistryGrain(params RunnerInfo[] runners)
            : this((IReadOnlyList<RunnerInfo>)runners) { }

        public FakeRunnerRegistryGrain(IReadOnlyList<RunnerInfo> runners)
        {
            _runners = runners;
        }

        public Task<IReadOnlyList<RunnerInfo>> ListRunnersAsync() =>
            Task.FromResult(_runners);

        public Task RegisterAsync(RunnerInfo info) => throw new NotSupportedException();
        public Task UnregisterAsync(string runnerId) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListRunnerIdsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListCoderModelsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListCoderModelsByRuntimeAsync(string runtime) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string[]>> ListCoderModelVariantsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string[]>> ListCoderModelVariantsByRuntimeAsync(string runtime) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string[]>> ListCoderReasoningEffortsByRuntimeAsync(string runtime) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunnerInfo>> ListAllAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<RunnerInfo>> ListEligibleRunnersAsync(string projectId) => throw new NotSupportedException();
    }

    private sealed class RegistryGrainFactory : IGrainFactory
    {
        private readonly IRunnerRegistryGrain _registry;

        public RegistryGrainFactory(IRunnerRegistryGrain registry)
        {
            _registry = registry;
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IRunnerRegistryGrain))
                return (TGrainInterface)(object)_registry;
            throw new NotSupportedException($"{nameof(RegistryGrainFactory)} does not support {typeof(TGrainInterface).Name}");
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
}
