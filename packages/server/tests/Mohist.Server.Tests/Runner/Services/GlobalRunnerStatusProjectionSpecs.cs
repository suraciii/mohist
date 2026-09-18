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
        _definitions = new RunnerDefinitionStore(new TestDbContextFactory(_database.Options), _time);
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
        var observation = new RunnerStatusObservation(
            RunnerStatus.Online,
            Now,
            info,
            Draining: false,
            UpdateInterruptId: null,
            DispatchObservation: new RunnerDispatchObservation(
                connectionGeneration,
                AdmissionReady: true,
                AdmissionReasonCodes: [],
                RuntimeReadiness: [new RuntimeReadinessWitness("pi", true, 3)]));
        var works = new RunnerActiveWorkItem[]
        {
            new("workflow-work", WorkDispatchOwnerKinds.Workflow, "workflow-1", "task", "build", "Build"),
            new("agent-work", WorkDispatchOwnerKinds.AgentJob, "job-1", "agent-job", null, "Agent"),
        };
        var service = CreateService(runnerId, tracker, observation, works, RunnerCredentialStatus.Active);

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
        var service = CreateService(runnerId, new RunnerConnectionTracker(), null, [], RunnerCredentialStatus.Active);

        var row = Assert.Single((await service.GetGlobalRunnersAsync()).Runners);

        Assert.Equal("offline", row.Presence.State);
        Assert.Null(row.Capacity!.Used);
        Assert.Equal(3, row.Capacity.Total);
        var action = Assert.Single(row.NextActions);
        Assert.Equal("start-runner", action.Code);
        Assert.Equal("mo service start runner", action.Command);
    }

    [Fact]
    public async Task GlobalProjection_CurrentNegativeRuntimeWitnessPreservesGeneration()
    {
        const string runnerId = "runner-negative-runtime";
        await _definitions.GetOrInitAsync(runnerId);
        var tracker = new RunnerConnectionTracker();
        var connectionGeneration = tracker.Register(runnerId, "connection-negative");
        var observation = new RunnerStatusObservation(
            RunnerStatus.Online,
            Now,
            new RunnerInfo(runnerId, [], "negative-host", null, ConnectionGeneration: connectionGeneration),
            Draining: false,
            UpdateInterruptId: null,
            DispatchObservation: new RunnerDispatchObservation(
                connectionGeneration,
                AdmissionReady: true,
                AdmissionReasonCodes: [],
                RuntimeReadiness: [new RuntimeReadinessWitness("pi", Ready: false, Generation: 7)]));
        var service = CreateService(runnerId, tracker, observation, [], RunnerCredentialStatus.Active);

        var runtime = Assert.Single(Assert.Single((await service.GetGlobalRunnersAsync()).Runners).Runtimes);

        Assert.Equal("not-ready", runtime.Readiness.State);
        Assert.Equal(7, runtime.Readiness.Generation);
        Assert.Equal("runtime-reported-not-ready", runtime.Readiness.ReasonCode);
    }

    [Fact]
    public async Task GlobalProjection_EnvironmentSummaryContainsOnlyApplicationMetadata()
    {
        const string runnerId = "runner-environment-projection";
        await _definitions.GetOrInitAsync(runnerId);
        var loadedAt = Now.AddMinutes(-3);
        var tracker = new RunnerConnectionTracker();
        var connectionGeneration = tracker.Register(runnerId, "connection-environment");
        var updateId = Guid.NewGuid().ToString();
        var observation = new RunnerStatusObservation(
            RunnerStatus.Online,
            Now,
            new RunnerInfo(
                runnerId,
                [],
                "environment-host",
                null,
                ConnectionGeneration: connectionGeneration,
                EnvironmentVersion: "env-v2",
                EnvironmentLoadedAt: loadedAt),
            Draining: true,
            UpdateInterruptId: updateId,
            DispatchObservation: new RunnerDispatchObservation(
                connectionGeneration,
                AdmissionReady: false,
                AdmissionReasonCodes: ["draining"],
                RuntimeReadiness: []),
            EnvironmentApplication: new RunnerEnvironmentApplicationObservation(
                updateId,
                "env-v3",
                "env-v2",
                "applying",
                "PATH=/secret",
                "process-old",
                connectionGeneration,
                Now.AddMinutes(-1),
                null));
        var service = CreateService(runnerId, tracker, observation, [], RunnerCredentialStatus.Active);

        var row = Assert.Single((await service.GetGlobalRunnersAsync()).Runners);

        Assert.NotNull(row.Environment);
        Assert.Equal("env-v2", row.Environment!.ActiveVersion);
        Assert.Equal(loadedAt, row.Environment.ActiveLoadedAt);
        Assert.NotNull(row.Environment.Application);
        Assert.Equal(updateId, row.Environment.Application!.UpdateId);
        Assert.Equal("applying", row.Environment.Application.Phase);
        Assert.Equal("env-v3", row.Environment.Application.TargetVersion);
        Assert.Equal("env-v2", row.Environment.Application.PreviousVersion);
        Assert.Null(row.Environment.Application.FailureCode);
    }

    [Fact]
    public async Task GlobalProjection_ConfirmedRevocationShellQuotesValidRunnerId()
    {
        const string runnerId = "build runner'$(touch /tmp/owned);";
        await _definitions.GetOrInitAsync(runnerId);
        var service = CreateService(runnerId, new RunnerConnectionTracker(), null, [], RunnerCredentialStatus.Revoked);

        var row = Assert.Single((await service.GetGlobalRunnersAsync()).Runners);

        Assert.Contains("credential-revoked", row.Admission.ReasonCodes);
        var action = Assert.Single(row.NextActions);
        Assert.Equal("reenroll-runner", action.Code);
        Assert.Equal(
            "mo install runner --repo-root <path> --runner-id 'build runner'\"'\"'$(touch /tmp/owned);'",
            action.Command);
        Assert.DoesNotContain(row.NextActions, next => next.Code == "start-runner");
    }

    [Fact]
    public async Task GlobalAvailability_RuntimeRequirement_MirrorsClaimCapabilityGate()
    {
        const string runnerId = "runner-runtime-requirement";
        await _definitions.GetOrInitAsync(runnerId);
        var tracker = new RunnerConnectionTracker();
        var connectionGeneration = tracker.Register(runnerId, "runtime-connection");
        var info = new RunnerInfo(
            runnerId,
            ["spec/*"],
            "runtime-host",
            null,
            RuntimeCatalogs: new Dictionary<string, RuntimeCatalogEntry>
            {
                ["pi"] = new(
                    Models: ["openai/gpt-5"],
                    Variants: null,
                    SupportsReasoningEffort: false,
                    Complete: true,
                    CapabilityRevision: "rev"),
            },
            ConnectionGeneration: connectionGeneration);
        var observation = new RunnerStatusObservation(
            RunnerStatus.Online,
            Now,
            info,
            Draining: false,
            UpdateInterruptId: null,
            DispatchObservation: new RunnerDispatchObservation(
                connectionGeneration,
                AdmissionReady: true,
                AdmissionReasonCodes: [],
                RuntimeReadiness: [new RuntimeReadinessWitness("pi", Ready: true, Generation: 3)]));
        var service = CreateService(runnerId, tracker, observation, [], RunnerCredentialStatus.Active);
        var snapshot = await service.GetGlobalRunnersAsync();

        var ready = RunnerStatusService.ProjectAvailability(
            snapshot,
            new RunnerRuntimeRequirement("pi", "openai/gpt-5", null));
        Assert.True(ready.CanAcceptWork);

        var unknownModel = RunnerStatusService.ProjectAvailability(
            snapshot,
            new RunnerRuntimeRequirement("pi", "openai/gpt-6", null));
        Assert.False(unknownModel.CanAcceptWork);
        Assert.Equal("runtime-not-ready", unknownModel.BlockingReason);

        var missingRuntime = RunnerStatusService.ProjectAvailability(
            snapshot,
            new RunnerRuntimeRequirement("opencode", null, null));
        Assert.False(missingRuntime.CanAcceptWork);
        Assert.Equal("runtime-not-ready", missingRuntime.BlockingReason);
    }

    [Fact]
    public async Task GlobalProjection_OwnerReadFailure_ExcludesUnknownCapacityFromAggregate()
    {
        const string unknownId = "runner-unknown-usage";
        const string healthyId = "runner-known-usage";
        await _definitions.GetOrInitAsync(unknownId);
        await _definitions.GetOrInitAsync(healthyId);
        await _definitions.UpdateSlotsAsync(unknownId, 1);
        await _definitions.UpdateSlotsAsync(healthyId, 2);
        var tracker = new RunnerConnectionTracker();
        var unknownGeneration = tracker.Register(unknownId, "unknown-connection");
        var healthyGeneration = tracker.Register(healthyId, "healthy-connection");
        var observations = new RunnerStatusObservationStore();
        observations.Set(unknownId, OnlineObservation(unknownId, unknownGeneration));
        observations.Set(healthyId, OnlineObservation(healthyId, healthyGeneration));
        var service = new RunnerStatusService(
            null!,
            tracker,
            _time,
            _definitions,
            new StatusCredentialReader(new Dictionary<string, RunnerCredentialStatus>
            {
                [unknownId] = RunnerCredentialStatus.Active,
                [healthyId] = RunnerCredentialStatus.Active,
            }),
            observations,
            new FailingActiveWorkReader(
                new Dictionary<string, IReadOnlyList<RunnerActiveWorkItem>>
                {
                    [healthyId] =
                    [
                        new RunnerActiveWorkItem(
                            "work-1",
                            WorkDispatchOwnerKinds.AgentJob,
                            "job-1",
                            "agent-job",
                            null,
                            "Agent"),
                    ],
                },
                new HashSet<string>([unknownId], StringComparer.Ordinal)));

        var snapshot = await service.GetGlobalRunnersAsync();
        var unknownRow = snapshot.Runners.Single(row => row.Identity.Id == unknownId);
        Assert.Null(unknownRow.Capacity!.Used);

        var availability = RunnerStatusService.ProjectAvailability(snapshot);

        Assert.True(availability.CapacityIncomplete);
        // Unknown usage is excluded rather than counted as zero used with its
        // slots apparently free: only the healthy Runner's 1/2 is aggregated.
        Assert.Equal(1, availability.Capacity.UsedSlots);
        Assert.Equal(2, availability.Capacity.TotalSlots);
        Assert.True(availability.CanAcceptWork);
    }

    private static RunnerStatusObservation OnlineObservation(string runnerId, string connectionGeneration) =>
        new(
            RunnerStatus.Online,
            Now,
            new RunnerInfo(runnerId, [], $"{runnerId}-host", null, ConnectionGeneration: connectionGeneration),
            Draining: false,
            UpdateInterruptId: null,
            DispatchObservation: new RunnerDispatchObservation(
                connectionGeneration,
                AdmissionReady: true,
                AdmissionReasonCodes: [],
                RuntimeReadiness: []));

    private sealed class FailingActiveWorkReader(
        IReadOnlyDictionary<string, IReadOnlyList<RunnerActiveWorkItem>> works,
        IReadOnlySet<string> failing) : IRunnerActiveWorkReader
    {
        public Task<IReadOnlyList<RunnerActiveWorkItem>> ListAsync(string runnerId, CancellationToken ct = default)
        {
            if (failing.Contains(runnerId))
                throw new InvalidOperationException("owner ledger unavailable");
            return Task.FromResult(works.TryGetValue(runnerId, out var value)
                ? value
                : (IReadOnlyList<RunnerActiveWorkItem>)[]);
        }
    }

    private RunnerStatusService CreateService(
        string runnerId,
        RunnerConnectionTracker tracker,
        RunnerStatusObservation? observation,
        IReadOnlyList<RunnerActiveWorkItem> works,
        RunnerCredentialStatus credentialStatus)
    {
        var observations = new RunnerStatusObservationStore();
        if (observation is not null)
            observations.Set(runnerId, observation);
        return new RunnerStatusService(
            null!,
            tracker,
            _time,
            _definitions,
            new StatusCredentialReader(new Dictionary<string, RunnerCredentialStatus> { [runnerId] = credentialStatus }),
            observations,
            new StubActiveWorkReader(new Dictionary<string, IReadOnlyList<RunnerActiveWorkItem>> { [runnerId] = works }));
    }

    private sealed class StubActiveWorkReader(
        IReadOnlyDictionary<string, IReadOnlyList<RunnerActiveWorkItem>> values) : IRunnerActiveWorkReader
    {
        public Task<IReadOnlyList<RunnerActiveWorkItem>> ListAsync(string runnerId, CancellationToken ct = default) =>
            Task.FromResult(values.TryGetValue(runnerId, out var works)
                ? works
                : (IReadOnlyList<RunnerActiveWorkItem>)[]);
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
