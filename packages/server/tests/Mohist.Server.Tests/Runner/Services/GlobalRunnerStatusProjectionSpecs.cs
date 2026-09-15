using System.Reflection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Runner.Services;

[Trait("level", "L0")]
public sealed class GlobalRunnerStatusProjectionSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateMigrated();
    private readonly FixedTimeProvider _time = new(Now);
    private readonly RunnerDefinitionStore _definitions;

    public GlobalRunnerStatusProjectionSpecs()
    {
        _definitions = new RunnerDefinitionStore(
            new TestDbContextFactory(_database.Options),
            _time);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task GlobalProjection_UsesMixedOwnerSnapshot_CurrentCatalogAndBuildIdentity()
    {
        const string runnerId = "runner-global-projection";
        await _definitions.GetOrInitAsync(runnerId);
        await _definitions.UpdateSlotsAsync(runnerId, 3);

        var tracker = new RunnerConnectionTracker();
        var connectionGeneration = tracker.Register(runnerId, "connection-1");
        var info = new RunnerInfo(
            runnerId,
            ["spec/*"],
            "build-host",
            null,
            RuntimeCatalogs: new Dictionary<string, RuntimeCatalogEntry>
            {
                ["pi"] = new(
                    Models: ["openai/gpt-5"],
                    Variants: new Dictionary<string, string[]> { ["openai/gpt-5"] = ["high"] },
                    SupportsReasoningEffort: true,
                    Complete: true,
                    CapabilityRevision: "catalog-rev",
                    ReasoningEfforts: new Dictionary<string, string[]> { ["openai/gpt-5"] = ["high"] }),
            },
            Component: "mohist-runner",
            SourceRevision: "source-rev",
            ReleaseId: "release-42",
            Generation: 7,
            ConnectionGeneration: connectionGeneration);
        var runtime = new RunnerRuntimeState(
            RunnerStatus.Online,
            Now,
            [
                new RunnerActiveWorkItem("workflow-work", WorkDispatchOwnerKinds.Workflow, "workflow-1", "task", "build", "Build"),
                new RunnerActiveWorkItem("agent-work", WorkDispatchOwnerKinds.AgentJob, "job-1", "agent-job", null, "Agent"),
            ],
            DispatchObservation: new RunnerDispatchObservation(
                connectionGeneration,
                AdmissionReady: true,
                AdmissionReasonCodes: [],
                RuntimeReadiness: [new RuntimeReadinessWitness("pi", true, 3)]));
        var (service, _) = CreateService(
            runnerId,
            tracker,
            new StatusRunnerProxy { Info = info, Runtime = runtime },
            new Dictionary<string, RunnerCredentialStatus> { [runnerId] = RunnerCredentialStatus.Active });

        var snapshot = await service.GetGlobalRunnersAsync();
        var row = Assert.Single(snapshot.Runners);

        Assert.Equal(Now, snapshot.ObservedAt);
        Assert.Equal(runnerId, row.Identity.Id);
        Assert.Equal("build-host", row.Identity.Hostname);
        Assert.Equal("mohist-runner", row.Identity.Component);
        Assert.Equal("source-rev", row.Identity.SourceRevision);
        Assert.Equal("release-42", row.Identity.ReleaseId);
        Assert.Equal(7, row.Identity.Generation);
        Assert.Equal("online", row.Presence.State);
        Assert.Equal("connected", row.Control.State);
        Assert.Equal("ready", row.Admission.State);
        Assert.Empty(row.Admission.ReasonCodes);
        Assert.Equal(2, row.Capacity!.Used);
        Assert.Equal(3, row.Capacity.Total);
        Assert.Equal([WorkDispatchOwnerKinds.Workflow, WorkDispatchOwnerKinds.AgentJob], row.ActiveWorks.Select(work => work.OwnerKind));
        Assert.Equal(["workflow-work", "agent-work"], row.ActiveWorks.Select(work => work.WorkId));
        Assert.Equal("ready", Assert.Single(row.Runtimes).Readiness.State);
        Assert.Equal(3, Assert.Single(row.Runtimes).Readiness.Generation);
        Assert.Equal(1, Assert.Single(row.Runtimes).Catalog!.ModelCount);
        Assert.Equal("catalog-rev", Assert.Single(row.Runtimes).Catalog!.CapabilityRevision);
        Assert.Empty(row.NextActions);
    }

    [Fact]
    public async Task GlobalProjection_OfflineDefinitionKeepsConfiguredCapacityAndStartAction()
    {
        const string runnerId = "runner-offline-definition";
        await _definitions.GetOrInitAsync(runnerId);
        await _definitions.UpdateSlotsAsync(runnerId, 3);
        var (service, _) = CreateService(
            runnerId,
            new RunnerConnectionTracker(),
            new StatusRunnerProxy(),
            new Dictionary<string, RunnerCredentialStatus> { [runnerId] = RunnerCredentialStatus.Active });

        var row = Assert.Single((await service.GetGlobalRunnersAsync()).Runners);

        Assert.Equal("offline", row.Presence.State);
        Assert.Null(row.Capacity!.Used);
        Assert.Equal(3, row.Capacity.Total);
        var action = Assert.Single(row.NextActions);
        Assert.Equal("start-runner", action.Code);
        Assert.Equal("mo service start runner", action.Command);
    }

    [Fact]
    public async Task GlobalProjection_ConfirmedRevocationWinsOverStartGuidance()
    {
        const string runnerId = "runner-revoked-credential";
        await _definitions.GetOrInitAsync(runnerId);
        var (service, _) = CreateService(
            runnerId,
            new RunnerConnectionTracker(),
            new StatusRunnerProxy(),
            new Dictionary<string, RunnerCredentialStatus> { [runnerId] = RunnerCredentialStatus.Revoked });

        var row = Assert.Single((await service.GetGlobalRunnersAsync()).Runners);

        Assert.Contains("credential-revoked", row.Admission.ReasonCodes);
        var action = Assert.Single(row.NextActions);
        Assert.Equal("reenroll-runner", action.Code);
        Assert.DoesNotContain(row.NextActions, next => next.Code == "start-runner");
    }

    private (RunnerStatusService Service, StatusRunnerProxy Runner) CreateService(
        string runnerId,
        RunnerConnectionTracker tracker,
        StatusRunnerProxy runner,
        IReadOnlyDictionary<string, RunnerCredentialStatus> credentials)
    {
        var grain = DispatchProxy.Create<IRunnerGrain, StatusRunnerProxy>();
        var grainProxy = (StatusRunnerProxy)(object)grain;
        grainProxy.Id = runner.Id;
        grainProxy.Info = runner.Info;
        grainProxy.Runtime = runner.Runtime;

        var factory = DispatchProxy.Create<IGrainFactory, StatusGrainFactory>();
        var factoryProxy = (StatusGrainFactory)(object)factory;
        factoryProxy.Runners[runnerId] = grainProxy;
        var service = new RunnerStatusService(
            factory,
            tracker,
            _time,
            _definitions,
            new StatusCredentialReader(credentials));
        return (service, grainProxy);
    }

    private class StatusGrainFactory : DispatchProxy
    {
        public Dictionary<string, StatusRunnerProxy> Runners { get; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain)
                && targetMethod.IsGenericMethod
                && targetMethod.GetGenericArguments()[0] == typeof(IRunnerGrain)
                && args is { Length: > 0 }
                && args[0] is string runnerId)
                return Runners[runnerId];

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private class StatusRunnerProxy : DispatchProxy
    {
        public string Id { get; set; } = "runner-offline-definition";
        public RunnerInfo? Info { get; set; }
        public RunnerRuntimeState? Runtime { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IRunnerGrain.GetInfoAsync) => Task.FromResult(Info),
            nameof(IRunnerGrain.GetRuntimeStateAsync) => Task.FromResult(Runtime!),
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }

    private sealed class StatusCredentialReader(IReadOnlyDictionary<string, RunnerCredentialStatus> values)
        : IRunnerCredentialStatusReader
    {
        public Task<RunnerCredentialStatus> GetStatusAsync(string runnerId, CancellationToken ct = default) =>
            Task.FromResult(values.TryGetValue(runnerId, out var status)
                ? status
                : RunnerCredentialStatus.Missing);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
