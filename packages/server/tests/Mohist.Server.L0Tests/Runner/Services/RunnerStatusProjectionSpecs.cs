using System.Reflection;
using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain.Run;
using Orleans;
using Xunit;

namespace Mohist.Server.L0Tests.Runner.Services;

[Trait("level", "L0")]
public sealed class RunnerStatusProjectionSpecs
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetRunnersAsync_GlobalRunner_IsIncluded()
    {
        var runnerId = "runner-global";
        var (service, _) = CreateService(Runner(runnerId, projectId: null));

        var result = await service.GetRunnersAsync("test-project");

        Assert.Contains(result, r => r.Id == runnerId);
    }

    [Fact]
    public async Task GetRunnersAsync_ProjectRunner_IsIncluded()
    {
        var runnerId = "runner-project";
        var (service, _) = CreateService(Runner(runnerId, "test-project"));

        var result = await service.GetRunnersAsync("test-project");

        Assert.Contains(result, r => r.Id == runnerId);
    }

    [Fact]
    public async Task GetRunnersAsync_OtherProjectRunner_IsIncluded()
    {
        var runnerId = "runner-other";
        var (service, _) = CreateService(Runner(runnerId, "other-project"));

        var result = await service.GetRunnersAsync("test-project");

        Assert.Contains(result, r => r.Id == runnerId);
    }

    [Fact]
    public async Task GetRunnersAsync_GlobalRegistry_ReturnsRunnersRegardlessOfProjectIdField()
    {
        var runnerId = "runner-global-scope";
        var (service, _) = CreateService(Runner(runnerId, "some-project"));

        var result = await service.GetRunnersAsync("different-project");

        Assert.Contains(result, r => r.Id == runnerId);
    }

    [Fact]
    public async Task GetRunnersAsync_IdleRunner_HasIdleStatus()
    {
        var (service, _) = CreateService(Runner("runner-idle", "test-project"));

        var result = await service.GetRunnersAsync("test-project");

        Assert.Equal("idle", Assert.Single(result).Status);
    }

    [Fact]
    public async Task GetRunnersAsync_BusyRunner_HasBusyStatusAndActiveWork()
    {
        var workflowId = "workflow-busy";
        var activeWork = Work(workflowId);
        var (service, _) = CreateService(Runner("runner-busy", "test-project"), activeWork: [activeWork]);

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Equal("busy", view.Status);
        Assert.Equal(workflowId, Assert.Single(view.ActiveWorks).OwnerId);
    }

    [Fact]
    public async Task GetRunnersAsync_StaleRunner_HasStaleStatus()
    {
        var info = Runner("runner-stale", "test-project", registeredAt: Now.AddMinutes(-10));
        var (service, _) = CreateService(info, now: Now.AddMinutes(5));

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Equal("stale", view.Status);
    }

    [Fact]
    public async Task GetRunnersAsync_OfflineRunner_HasOfflineStatus()
    {
        var (service, _) = CreateService(Runner("runner-offline", "test-project"), status: RunnerStatus.Offline);

        var result = await service.GetRunnersAsync("test-project");

        Assert.Equal("offline", Assert.Single(result).Status);
    }

    [Fact]
    public async Task GetRunnersAsync_ConnectedRunner_HasConnectionState()
    {
        var tracker = new RunnerConnectionTracker();
        tracker.Register("runner-connected", "connection-1");
        var (service, _) = CreateService(Runner("runner-connected", "test-project"), tracker: tracker);

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Equal("connected", view.ConnectionState);
    }

    [Fact]
    public async Task GetRunnersAsync_DisconnectedRunner_HasDisconnectedConnectionState()
    {
        var (service, _) = CreateService(Runner("runner-disconnected", "test-project"));

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Equal("disconnected", view.ConnectionState);
    }

    [Fact]
    public async Task GetRunnersAsync_DisconnectedIdleWorkspaceRunner_IsProjectedAsOffline()
    {
        var info = Runner("runner-disconnected-workspace", "test-project", capabilities: ["spec/*", "workspace-query"]);
        var (service, _) = CreateService(info);

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Equal("disconnected", view.ConnectionState);
        Assert.Equal("offline", view.Status);
    }

    [Fact]
    public async Task GetRunnersAsync_DisconnectedBusyWorkspaceRunner_IsProjectedAsBusyWithActiveWorkDiagnostic()
    {
        var info = Runner("runner-disconnected-busy", "test-project", capabilities: ["spec/*", "workspace-query"]);
        var activeWork = Work("workflow-disconnected-busy");
        var (service, _) = CreateService(info, activeWork: [activeWork]);

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Equal("disconnected", view.ConnectionState);
        Assert.Equal("busy", view.Status);
        Assert.Equal("workflow-disconnected-busy", Assert.Single(view.ActiveWorks).OwnerId);
    }

    [Fact]
    public async Task GetRunnersAsync_CapacityUsesPersistedSlots()
    {
        var (service, _) = CreateService(Runner("runner-capacity", "test-project"), slots: 4);

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.NotNull(view.Capacity);
        Assert.Equal(4, view.Capacity.TotalSlots);
    }

    [Fact]
    public async Task GetRunnersAsync_ReturnsAllFields()
    {
        var registeredAt = Now.AddMinutes(-5);
        var info = Runner(
            "runner-full",
            "test-project",
            capabilities: ["spec/*", "workflow"],
            coderModels: ["openai/gpt-4", "anthropic/claude-3"],
            kind: "external",
            registeredAt: registeredAt);
        var tracker = new RunnerConnectionTracker();
        var (service, _) = CreateService(info, tracker: tracker);

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Equal("runner-full", view.Id);
        Assert.Equal("external", view.Kind);
        Assert.Equal("runner-full-host", view.Hostname);
        Assert.Equal("global", view.Scope.Type);
        Assert.Equal("idle", view.Status);
        Assert.Equal(registeredAt, view.RegisteredAt);
        Assert.Equal(Now, view.LastHeartbeatAt);
        Assert.Equal(["spec/*", "workflow"], view.Capabilities);
        Assert.Equal(["openai/gpt-4", "anthropic/claude-3"], view.CoderModels);
        Assert.Equal(2, view.CoderModelCount);
        Assert.NotNull(view.Capacity);
        Assert.Equal(0, view.Capacity.UsedSlots);
        Assert.Equal(1, view.Capacity.TotalSlots);
        Assert.Empty(view.ActiveWorks);
    }

    [Fact]
    public async Task GetRunnersAsync_GlobalRunnerScope_IsGlobal()
    {
        var (service, _) = CreateService(Runner("runner-scope", null));

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Equal("global", view.Scope.Type);
    }

    [Fact]
    public async Task GetRunnersAsync_DoesNotExposeSecrets()
    {
        var (service, _) = CreateService(Runner("runner-safe", "test-project"));

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));
        var json = JsonSerializer.Serialize(view);

        Assert.DoesNotContain("env", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRuntimeStateAsync_BusyRunner_ReportsBusyStatus()
    {
        var (service, _) = CreateService(Runner("runner-busy-state", "test-project"), activeWork: [Work("workflow-busy-state")]);

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Equal("busy", view.Status);
    }

    [Fact]
    public async Task GetRuntimeStateAsync_OnlineIdleRunner_ReportsIdleStatus()
    {
        var (service, _) = CreateService(Runner("idle-state-status-runner", "test-project"));

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Equal("idle", view.Status);
    }

    [Fact]
    public async Task ProjectRunnerAsync_BusyMultiSlotRunner_ProjectsEveryActiveWorkIntoList()
    {
        var activeWorks = new[]
        {
            Work("workflow-a", workId: "task-a.1", stage: "build", title: "Task A", issue: new("test-project", 1)),
            Work("workflow-b", workId: "task-b.1", stage: "review", title: "Task B", issue: new("test-project", 1)),
        };
        var (service, _) = CreateService(Runner("runner-multi-proj", "test-project"), slots: 2, activeWork: activeWorks);

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Equal(2, view.ActiveWorks.Count);
        Assert.Contains("workflow-a", view.ActiveWorks.Select(w => w.OwnerId));
        Assert.Contains("workflow-b", view.ActiveWorks.Select(w => w.OwnerId));
        Assert.All(view.ActiveWorks, work =>
        {
            Assert.Equal(WorkDispatchOwnerKinds.Workflow, work.OwnerKind);
            Assert.False(string.IsNullOrWhiteSpace(work.WorkId));
            Assert.Equal("task", work.WorkType);
            Assert.False(string.IsNullOrWhiteSpace(work.Stage));
            Assert.False(string.IsNullOrWhiteSpace(work.Title));
            Assert.NotNull(work.Issue);
            Assert.Equal(1, work.Issue!.IssueNumber);
        });
    }

    [Fact]
    public async Task ProjectRunnerAsync_BusyRunnerWithIssue_ProjectsIssueReference()
    {
        var issue = new WorkIssueRef("test-project", 9);
        var (service, _) = CreateService(Runner("runner-issue-proj", "test-project"), activeWork: [Work("workflow-issue-proj", issue: issue)]);

        var work = Assert.Single(Assert.Single(await service.GetRunnersAsync("test-project")).ActiveWorks);

        Assert.Equal("workflow-issue-proj", work.OwnerId);
        Assert.NotNull(work.Issue);
        Assert.Equal(issue.ProjectId, work.Issue!.ProjectId);
        Assert.Equal(issue.IssueNumber, work.Issue.IssueNumber);
    }

    [Fact]
    public async Task ProjectRunnerAsync_IdleRunner_HasEmptyActiveWorksList()
    {
        var (service, _) = CreateService(Runner("runner-empty-proj", "test-project"));

        var view = Assert.Single(await service.GetRunnersAsync("test-project"));

        Assert.Empty(view.ActiveWorks);
    }

    [Fact]
    public async Task GetRunnerAsync_KnownRunner_ReturnsFullDetail()
    {
        var info = Runner("runner-getasync", "test-project", coderModels: ["openai/gpt-4"], kind: "external");
        var (service, _) = CreateService(info);

        var view = await service.GetRunnerAsync("test-project", info.RunnerId);

        Assert.NotNull(view);
        Assert.Equal(info.RunnerId, view!.Id);
        Assert.Equal("external", view.Kind);
        Assert.Equal(info.Hostname, view.Hostname);
        Assert.Empty(view.ActiveWorks);
    }

    [Fact]
    public async Task GetRunnerAsync_UnknownRunnerId_ReturnsNull()
    {
        var (service, _) = CreateService(Runner("known", "test-project"));

        var view = await service.GetRunnerAsync("test-project", "runner-unknown");

        Assert.Null(view);
    }

    [Fact]
    public async Task GetRunnerAsync_RunnerWithDifferentProjectId_ReturnsRunner()
    {
        var info = Runner("runner-other-proj", "other-project");
        var (service, _) = CreateService(info);

        var view = await service.GetRunnerAsync("test-project", info.RunnerId);

        Assert.NotNull(view);
        Assert.Equal(info.RunnerId, view!.Id);
    }

    [Fact]
    public async Task GetRunnerAsync_EmptyRunnerId_ReturnsNull()
    {
        var (service, _) = CreateService(Runner("known", "test-project"));

        var view = await service.GetRunnerAsync("test-project", string.Empty);

        Assert.Null(view);
    }

    [Fact]
    public async Task GetRunnerAsync_IsReadOnly_DoesNotMutateRunnerState()
    {
        var info = Runner("runner-readonly", "test-project");
        var activeWork = Work("workflow-readonly");
        var (service, runner) = CreateService(info, activeWork: [activeWork]);
        var runnerGrain = (IRunnerGrain)(object)runner;
        var beforeRuntime = await runnerGrain.GetRuntimeStateAsync();
        var beforeInfo = info;

        Assert.NotNull(await service.GetRunnerAsync("test-project", info.RunnerId));

        var afterRuntime = await runnerGrain.GetRuntimeStateAsync();
        Assert.Equal(beforeRuntime.LastHeartbeatAt, afterRuntime.LastHeartbeatAt);
        Assert.Equal(beforeRuntime.ActiveWorks, afterRuntime.ActiveWorks);
        Assert.Equal(beforeInfo.RegisteredAt, info.RegisteredAt);
    }

    [Fact]
    public async Task GetCapacityAsync_AcrossOnlineRunners_SumsUsedAndTotalSlots()
    {
        var (service, _) = CreateService(
            Runner("runner-cap-a", "test-project"),
            Runner("runner-cap-b", "test-project"),
            slots: 2,
            activeWork: [Work("workflow-a")],
            secondSlots: 4,
            secondActiveWork: [Work("workflow-b1"), Work("workflow-b2")]);

        var capacity = await service.GetCapacityAsync("test-project");

        Assert.Equal(3, capacity.UsedSlots);
        Assert.Equal(6, capacity.TotalSlots);
    }

    [Fact]
    public async Task GetCapacityAsync_ExcludesRunnersNotRegisteredThroughRunnerGrain()
    {
        var info = Runner("runner-orphan", "test-project");
        var (service, _) = CreateService(info, runtime: null, runnerAvailable: false);

        var capacity = await service.GetCapacityAsync("test-project");

        Assert.Equal(0, capacity.UsedSlots);
        Assert.Equal(0, capacity.TotalSlots);
    }

    [Fact]
    public async Task GetOnlineRunnersAsync_OnlyReturnsRegisteredGrainsWithOnlineStatus()
    {
        var online = Runner("runner-online", "test-project");
        var orphan = Runner("runner-online-orphan", "test-project");
        var harness = CreateHarness([online, orphan], new Dictionary<string, RunnerRuntimeState?>
        {
            [online.RunnerId] = Runtime([]),
            [orphan.RunnerId] = null,
        }, unavailable: new HashSet<string>([orphan.RunnerId], StringComparer.Ordinal));

        var views = await harness.Service.GetOnlineRunnersAsync("test-project");

        Assert.Single(views);
        Assert.Equal(online.RunnerId, views[0].Id);
    }

    [Fact]
    public async Task GetCapacityAsync_RunnerActiveWorksExceedVisibleSessions_CapacityFollowsRunner()
    {
        var (service, _) = CreateService(
            Runner("runner-div", "test-project"),
            slots: 5,
            activeWork: [Work("workflow-div-a"), Work("workflow-div-b")]);

        var capacity = await service.GetCapacityAsync("test-project");

        Assert.Equal(2, capacity.UsedSlots);
        Assert.Equal(5, capacity.TotalSlots);
    }

    private static RunnerInfo Runner(
        string runnerId,
        string? projectId,
        string[]? capabilities = null,
        string[]? coderModels = null,
        string kind = "external",
        DateTimeOffset? registeredAt = null)
        => new(
            runnerId,
            capabilities ?? ["spec/*"],
            $"{runnerId}-host",
            projectId,
            coderModels ?? ["openai/gpt-4"],
            kind,
            registeredAt ?? Now);

    private static RunnerActiveWorkItem Work(
        string ownerId,
        string workId = "task-1.1",
        string workType = "task",
        string stage = "build",
        string title = "Task 1",
        WorkIssueRef? issue = null)
        => new(workId, WorkDispatchOwnerKinds.Workflow, ownerId, workType, stage, title, issue);

    private static RunnerRuntimeState Runtime(IReadOnlyList<RunnerActiveWorkItem> activeWorks, RunnerStatus status = RunnerStatus.Online)
        => new(status, Now, activeWorks);

    private static (RunnerStatusService Service, StatusRunnerProxy Runner) CreateService(
        RunnerInfo info,
        RunnerInfo? second = null,
        DateTimeOffset? now = null,
        RunnerStatus status = RunnerStatus.Online,
        int slots = 1,
        IReadOnlyList<RunnerActiveWorkItem>? activeWork = null,
        RunnerRuntimeState? runtime = null,
        bool runnerAvailable = true,
        RunnerConnectionTracker? tracker = null,
        int? secondSlots = null,
        IReadOnlyList<RunnerActiveWorkItem>? secondActiveWork = null)
    {
        var infos = second is null ? [info] : new[] { info, second };
        var runtimes = new Dictionary<string, RunnerRuntimeState?>
        {
            [info.RunnerId] = runtime ?? Runtime(activeWork ?? [], status),
        };
        if (second is not null)
            runtimes[second.RunnerId] = Runtime(secondActiveWork ?? []);
        var result = CreateHarness(infos, runtimes, now ?? Now, slots, runnerAvailable: runnerAvailable, tracker: tracker);
        if (second is not null && secondSlots.HasValue)
            result.Factory.Runners[second.RunnerId].Slots = secondSlots.Value;
        return (result.Service, result.Runner);
    }

    private static (RunnerStatusService Service, StatusRunnerProxy Runner, StatusTestGrainFactory Factory) CreateHarness(
        IReadOnlyList<RunnerInfo> infos,
        Dictionary<string, RunnerRuntimeState?> runtimes,
        DateTimeOffset? now = null,
        int slots = 1,
        IReadOnlySet<string>? unavailable = null,
        bool runnerAvailable = true,
        RunnerConnectionTracker? tracker = null)
    {
        var factory = DispatchProxy.Create<IGrainFactory, StatusTestGrainFactory>();
        var proxy = (StatusTestGrainFactory)(object)factory;
        var registry = DispatchProxy.Create<IRunnerRegistryGrain, StatusRegistryProxy>();
        var registryProxy = (StatusRegistryProxy)(object)registry;
        registryProxy.Infos = infos;
        proxy.Registry = registry;
        foreach (var info in infos)
        {
            var runner = DispatchProxy.Create<IRunnerGrain, StatusRunnerProxy>();
            var runnerProxy = (StatusRunnerProxy)(object)runner;
            runnerProxy.Runtime = runtimes.TryGetValue(info.RunnerId, out var runtime) ? runtime : Runtime([]);
            runnerProxy.Slots = slots;
            runnerProxy.Unavailable = !runnerAvailable || unavailable?.Contains(info.RunnerId) == true;
            proxy.Runners[info.RunnerId] = runnerProxy;
        }

        var service = new RunnerStatusService(factory, tracker ?? new RunnerConnectionTracker(), new FixedTimeProvider(now ?? Now));
        return (service, proxy.Runners[infos[0].RunnerId], proxy);
    }

    private class StatusTestGrainFactory : DispatchProxy
    {
        public IRunnerRegistryGrain Registry { get; set; } = null!;
        public Dictionary<string, StatusRunnerProxy> Runners { get; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain) && targetMethod.IsGenericMethod)
            {
                var type = targetMethod.GetGenericArguments()[0];
                if (type == typeof(IRunnerRegistryGrain))
                    return Registry;
                if (type == typeof(IRunnerGrain) && args is { Length: > 0 } && args[0] is string runnerId)
                    return Runners[runnerId];
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private class StatusRegistryProxy : DispatchProxy
    {
        public IReadOnlyList<RunnerInfo> Infos { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(IRunnerRegistryGrain.ListEligibleRunnersAsync)
                ? Task.FromResult(Infos)
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private class StatusRunnerProxy : DispatchProxy
    {
        public RunnerRuntimeState? Runtime { get; set; }
        public int Slots { get; set; } = 1;
        public bool Unavailable { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IRunnerGrain.GetRuntimeStateAsync))
            {
                if (Unavailable)
                    return Task.FromException<RunnerRuntimeState>(new InvalidOperationException("runner unavailable"));
                return Task.FromResult(Runtime!);
            }
            if (targetMethod?.Name == nameof(IRunnerGrain.GetSlotsAsync))
            {
                if (Unavailable)
                    return Task.FromException<int>(new InvalidOperationException("runner unavailable"));
                return Task.FromResult(Slots);
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
