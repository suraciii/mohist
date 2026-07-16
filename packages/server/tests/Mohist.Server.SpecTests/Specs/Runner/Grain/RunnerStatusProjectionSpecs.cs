using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerStatusProjectionSpecs : WorkflowGrainSpecs
{
    public RunnerStatusProjectionSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private static RunnerStatusService CreateService(IGrainFactory grains, RunnerConnectionTracker tracker, TimeProvider timeProvider)
    {
        return new RunnerStatusService(grains, tracker, timeProvider);
    }

    private static FixedTimeProvider TimeAt(DateTimeOffset now)
    {
        return new FixedTimeProvider(now);
    }

    private async Task<WorkDispatch> StartIssueWorkflowWorkAsync(
        string runnerId,
        string workflowId,
        string projectId,
        int issueNumber,
        string title = "Issue Task")
    {
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, SingleStage(
            tasks: [new("task-1", title, "spec/task")],
            checks: []), projectId);
        await workflow.StartAsync(new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: _fixture.TimeProvider.GetUtcNow(),
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectId"] = projectId,
                ["issueNumber"] = issueNumber.ToString(),
            })));
        await workflow.AssignWorkerAsync(runnerId);

        var work = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(Services);
        Assert.NotNull(work);
        return work;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_GlobalRunner_IsIncluded()
    {
        var runnerId = $"runner-global-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "global-host", null, ["openai/gpt-4"]));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        Assert.Contains(result, r => r.Id == runnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_ProjectRunner_IsIncluded()
    {
        var runnerId = $"runner-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "project-host", "test-project", ["openai/gpt-4"]));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        Assert.Contains(result, r => r.Id == runnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_OtherProjectRunner_IsIncluded()
    {
        var runnerId = $"runner-other-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "other-host", "other-project", ["openai/gpt-4"]));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        Assert.Contains(result, r => r.Id == runnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_GlobalRegistry_ReturnsRunnersRegardlessOfProjectIdField()
    {
        var runnerId = $"runner-global-scope-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "scope-host", "some-project"));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("different-project");

        Assert.Contains(result, r => r.Id == runnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_IdleRunner_HasIdleStatus()
    {
        var runnerId = $"runner-idle-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "idle-host", "test-project"));
        await runner.HeartbeatAsync();

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal("idle", view.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_BusyRunner_HasBusyStatusAndActiveWork()
    {
        var runnerId = $"runner-busy-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "busy-host", "test-project"));

        var workflowId = $"wf-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "task-1.1", "task", "build", "Task 1");

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal("busy", view.Status);
        var activeWork = Assert.Single(view.ActiveWorks);
        Assert.Equal(workflowId, activeWork.OwnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_StaleRunner_HasStaleStatus()
    {
        var runnerId = $"runner-stale-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registeredAt = _fixture.TimeProvider.GetUtcNow().AddMinutes(-10);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "stale-host", "test-project", RegisteredAt: registeredAt));

        var now = _fixture.TimeProvider.GetUtcNow().AddMinutes(5);
        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(now));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal("stale", view.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_OfflineRunner_HasOfflineStatus()
    {
        var runnerId = $"runner-offline-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "offline-host", "test-project"));
        await runner.UnregisterAsync();

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        var view = result.FirstOrDefault(r => r.Id == runnerId);
        // After unregister, runner is removed from registry so it may not appear
        // This test verifies that if a runner grain is offline but still in registry,
        // it shows offline status
        Assert.True(view == null || view.Status == "offline");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_ConnectedRunner_HasConnectionState()
    {
        var runnerId = $"runner-conn-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "conn-host", "test-project"));

        var tracker = new RunnerConnectionTracker();
        tracker.Register(runnerId, "conn-123");

        var service = CreateService(Grains, tracker, TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal("connected", view.ConnectionState);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_DisconnectedRunner_HasDisconnectedConnectionState()
    {
        var runnerId = $"runner-disc-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "disc-host", "test-project"));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal("disconnected", view.ConnectionState);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_DisconnectedIdleWorkspaceRunner_IsProjectedAsOffline()
    {
        var runnerId = $"runner-disc-status-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*", "workspace-query"], "disc-status-host", "test-project"));
        await runner.HeartbeatAsync();

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal("disconnected", view.ConnectionState);
        Assert.Equal("offline", view.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_DisconnectedBusyWorkspaceRunner_IsProjectedAsBusyWithActiveWorkDiagnostic()
    {
        var runnerId = $"runner-disc-busy-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*", "workspace-query"], "disc-busy-host", "test-project"));
        await runner.HeartbeatAsync();

        var workflowId = $"wf-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "task-1.1", "task", "build", "Task 1");

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal("disconnected", view.ConnectionState);
        Assert.Equal("busy", view.Status);
        var activeWork = Assert.Single(view.ActiveWorks);
        Assert.Equal(workflowId, activeWork.OwnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_CapacityUsesPersistedSlots()
    {
        var runnerId = $"runner-cap-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "cap-host", "test-project"));
        await runner.UpdateAsync(4);

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.NotNull(view.Capacity);
        Assert.Equal(4, view.Capacity.TotalSlots);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_ReturnsAllFields()
    {
        var runnerId = $"runner-full-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registeredAt = _fixture.TimeProvider.GetUtcNow().AddMinutes(-5);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*", "workflow"], "full-host", "test-project", ["openai/gpt-4", "anthropic/claude-3"], "external", registeredAt));
        await runner.HeartbeatAsync();

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal(runnerId, view.Id);
        Assert.Equal("external", view.Kind);
        Assert.Equal("full-host", view.Hostname);
        // Runners are global execution resources; RunnerInfo.ProjectId is
        // preserved on the wire but does not influence the scope view.
        Assert.Equal("global", view.Scope.Type);
        Assert.Equal("idle", view.Status);
        Assert.NotNull(view.RegisteredAt);
        Assert.NotNull(view.LastHeartbeatAt);
        Assert.Equal(new[] { "spec/*", "workflow" }, view.Capabilities);
        Assert.Equal(new[] { "openai/gpt-4", "anthropic/claude-3" }, view.CoderModels);
        Assert.Equal(2, view.CoderModelCount);
        Assert.NotNull(view.Capacity);
        Assert.Equal(0, view.Capacity.UsedSlots);
        Assert.Equal(1, view.Capacity.TotalSlots);
        Assert.Empty(view.ActiveWorks);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_GlobalRunnerScope_IsGlobal()
    {
        var runnerId = $"runner-scope-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "scope-host", null));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal("global", view.Scope.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_DoesNotExposeSecrets()
    {
        var runnerId = $"runner-safe-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "safe-host", "test-project"));
        await runner.HeartbeatAsync();

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        var json = global::System.Text.Json.JsonSerializer.Serialize(view);
        Assert.DoesNotContain("env", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetRuntimeStateAsync_OnlineIdleRunner_ExposesEmptyActiveWorksList()
    {
        var runnerId = await RegisterRunnerAsync("idle-state-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var runtime = await runner.GetRuntimeStateAsync();

        Assert.NotNull(runtime.ActiveWorks);
        Assert.Empty(runtime.ActiveWorks);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetRuntimeStateAsync_BusyRunner_ExposesDispatchContextForActiveWork()
    {
        var runnerId = $"runner-active-ctx-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "active-ctx-host", "test-project"));

        var workflowId = $"wf-ctx-{Guid.NewGuid():N}";
        var issue = new WorkIssueRef("test-project", 42);
        var dispatch = await StartIssueWorkflowWorkAsync(
            runnerId,
            workflowId,
            issue.ProjectId,
            issue.IssueNumber,
            "Task 1");

        var runtime = await runner.GetRuntimeStateAsync();
        var active = Assert.Single(runtime.ActiveWorks);
        Assert.Equal(dispatch.WorkId, active.WorkId);
        Assert.Equal(WorkDispatchOwnerKinds.Workflow, active.OwnerKind);
        Assert.Equal(workflowId, active.OwnerId);
        Assert.Equal("task", active.WorkType);
        Assert.Equal("build", active.Stage);
        Assert.Equal("Task 1", active.Title);
        Assert.NotNull(active.Issue);
        Assert.Equal(issue.ProjectId, active.Issue!.ProjectId);
        Assert.Equal(issue.IssueNumber, active.Issue.IssueNumber);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetRuntimeStateAsync_BusyRunnerWithoutIssue_ExposesNullIssue()
    {
        var runnerId = $"runner-no-issue-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "no-issue-host", "test-project"));

        var workflowId = $"wf-no-issue-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "task-1.1", "task", "build", "Task 1");

        var runtime = await runner.GetRuntimeStateAsync();
        var active = Assert.Single(runtime.ActiveWorks);
        Assert.Null(active.Issue);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetRuntimeStateAsync_MultiSlotRunner_ExposesAllConcurrentWorks()
    {
        var projectId = "test-project-multi";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"runner-multi-{Guid.NewGuid():N}", maxWorkflowSlots: 2);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var workflowA = $"wf-multi-a-{Guid.NewGuid():N}";
        var workflowB = $"wf-multi-b-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowA);
        await AssignActiveWorkForTestAsync(runnerId, workflowB);

        var runtime = await runner.GetRuntimeStateAsync();

        Assert.Equal(2, runtime.ActiveWorks.Count);
        Assert.Contains(runtime.ActiveWorks, w => w.OwnerId == workflowA);
        Assert.Contains(runtime.ActiveWorks, w => w.OwnerId == workflowB);
        Assert.All(runtime.ActiveWorks, w =>
        {
            Assert.False(string.IsNullOrWhiteSpace(w.WorkId));
            Assert.Equal(WorkDispatchOwnerKinds.Workflow, w.OwnerKind);
        });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetRuntimeStateAsync_BusyRunner_ReportsBusyStatus()
    {
        var runnerId = $"runner-busy-state-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "busy-state-host", "test-project"));

        var workflowId = $"wf-busy-state-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "task-1.1", "task", "build", "Task 1");

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var view = Assert.Single(await service.GetRunnersAsync("test-project"), r => r.Id == runnerId);
        Assert.Equal("busy", view.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetRuntimeStateAsync_OnlineIdleRunner_ReportsIdleStatus()
    {
        var runnerId = await RegisterRunnerAsync("idle-state-status-runner");
        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var view = Assert.Single(await service.GetRunnersAsync("test-project"), r => r.Id == runnerId);
        Assert.Equal("idle", view.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ProjectRunnerAsync_BusyMultiSlotRunner_ProjectsEveryActiveWorkIntoList()
    {
        var runnerId = await RegisterRunnerForProjectAsync("test-project-multi-proj", $"runner-multi-proj-{Guid.NewGuid():N}", maxWorkflowSlots: 2);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var workflowA = $"wf-multi-proj-a-{Guid.NewGuid():N}";
        var workflowB = $"wf-multi-proj-b-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowA, "task-a.1", "task", "build", "Task A");
        await AssignActiveWorkForTestAsync(runnerId, workflowB, "task-b.1", "task", "review", "Task B");

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var view = Assert.Single(await service.GetRunnersAsync("test-project-multi-proj"), r => r.Id == runnerId);

        Assert.Equal(2, view.ActiveWorks.Count);
        var ownerIds = view.ActiveWorks.Select(w => w.OwnerId).ToArray();
        Assert.Contains(workflowA, ownerIds);
        Assert.Contains(workflowB, ownerIds);

        Assert.All(view.ActiveWorks, work =>
        {
            Assert.Equal(WorkDispatchOwnerKinds.Workflow, work.OwnerKind);
            Assert.False(string.IsNullOrWhiteSpace(work.WorkId));
            Assert.Equal("task", work.WorkType);
            Assert.False(string.IsNullOrWhiteSpace(work.Stage));
            Assert.False(string.IsNullOrWhiteSpace(work.Title));
            Assert.Null(work.Issue);
        });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ProjectRunnerAsync_BusyRunnerWithIssue_ProjectsIssueReference()
    {
        var runnerId = $"runner-issue-proj-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "issue-proj-host", "test-project"));

        var workflowId = $"wf-issue-proj-{Guid.NewGuid():N}";
        var issue = new WorkIssueRef("test-project", 9);
        await StartIssueWorkflowWorkAsync(
            runnerId,
            workflowId,
            issue.ProjectId,
            issue.IssueNumber);

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var view = Assert.Single(await service.GetRunnersAsync("test-project"), r => r.Id == runnerId);

        var work = Assert.Single(view.ActiveWorks);
        Assert.Equal(workflowId, work.OwnerId);
        Assert.NotNull(work.Issue);
        Assert.Equal(issue.ProjectId, work.Issue!.ProjectId);
        Assert.Equal(issue.IssueNumber, work.Issue.IssueNumber);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ProjectRunnerAsync_IdleRunner_HasEmptyActiveWorksList()
    {
        var runnerId = await RegisterRunnerAsync($"runner-empty-proj-{Guid.NewGuid():N}");
        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var view = Assert.Single(await service.GetRunnersAsync("test-project"), r => r.Id == runnerId);
        Assert.NotNull(view.ActiveWorks);
        Assert.Empty(view.ActiveWorks);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetRunnerAsync_KnownRunner_ReturnsFullDetail()
    {
        var runnerId = $"runner-getasync-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "getasync-host", "test-project", ["openai/gpt-4"], "external"));
        await runner.HeartbeatAsync();

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var view = await service.GetRunnerAsync("test-project", runnerId);

        Assert.NotNull(view);
        Assert.Equal(runnerId, view!.Id);
        Assert.Equal("external", view.Kind);
        Assert.Equal("getasync-host", view.Hostname);
        Assert.Empty(view.ActiveWorks);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetRunnerAsync_UnknownRunnerId_ReturnsNull()
    {
        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var view = await service.GetRunnerAsync("test-project", $"runner-unknown-{Guid.NewGuid():N}");
        Assert.Null(view);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetRunnerAsync_RunnerWithDifferentProjectId_ReturnsRunner()
    {
        var runnerId = $"runner-other-proj-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "other-proj-host", "other-project"));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var view = await service.GetRunnerAsync("test-project", runnerId);

        Assert.NotNull(view);
        Assert.Equal(runnerId, view!.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetRunnerAsync_EmptyRunnerId_ReturnsNull()
    {
        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var view = await service.GetRunnerAsync("test-project", string.Empty);
        Assert.Null(view);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetCapacityAsync_AcrossOnlineRunners_SumsUsedAndTotalSlots()
    {
        await ClearGlobalRunnerRegistryAsync();

        var slotsA = 2;
        var slotsB = 4;
        var runnerA = $"runner-cap-a-{Guid.NewGuid():N}";
        var runnerB = $"runner-cap-b-{Guid.NewGuid():N}";

        var grainA = Grains.GetGrain<IRunnerGrain>(runnerA);
        await grainA.RegisterAsync(new RunnerInfo(runnerA, ["spec/*"], "cap-a-host", "test-project"));
        await grainA.UpdateAsync(slotsA);
        var workflowA = $"wf-cap-a-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerA, workflowA);

        var grainB = Grains.GetGrain<IRunnerGrain>(runnerB);
        await grainB.RegisterAsync(new RunnerInfo(runnerB, ["spec/*"], "cap-b-host", "test-project"));
        await grainB.UpdateAsync(slotsB);
        var workflowB1 = $"wf-cap-b1-{Guid.NewGuid():N}";
        var workflowB2 = $"wf-cap-b2-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerB, workflowB1);
        await AssignActiveWorkForTestAsync(runnerB, workflowB2);

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var capacity = await service.GetCapacityAsync("test-project");

        Assert.Equal(3, capacity.UsedSlots);
        Assert.Equal(slotsA + slotsB, capacity.TotalSlots);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetCapacityAsync_ExcludesRunnersNotRegisteredThroughRunnerGrain()
    {
        await ClearGlobalRunnerRegistryAsync();

        var runnerId = $"runner-cap-orphan-{Guid.NewGuid():N}";
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        await registry.RegisterAsync(new RunnerInfo(runnerId, [], "orphan-host", "test-project"));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var capacity = await service.GetCapacityAsync("test-project");

        Assert.Equal(0, capacity.UsedSlots);
        Assert.Equal(0, capacity.TotalSlots);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetOnlineRunnersAsync_OnlyReturnsRegisteredGrainsWithOnlineStatus()
    {
        await ClearGlobalRunnerRegistryAsync();

        var onlineRunner = $"runner-online-{Guid.NewGuid():N}";
        await Grains.GetGrain<IRunnerGrain>(onlineRunner)
            .RegisterAsync(new RunnerInfo(onlineRunner, ["spec/*"], "online-host", "test-project"));

        var orphanRunner = $"runner-online-orphan-{Guid.NewGuid():N}";
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        await registry.RegisterAsync(new RunnerInfo(orphanRunner, [], "orphan-host", "test-project"));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var views = await service.GetOnlineRunnersAsync("test-project");

        Assert.Single(views);
        Assert.Equal(onlineRunner, views[0].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GetCapacityAsync_RunnerActiveWorksExceedVisibleSessions_CapacityFollowsRunner()
    {
        // The runner grain holds two active workflow works (distinct owner ids)
        // yet no AgentSession is visible to the server; capacity.active must
        // follow the runner active-works count, not be clamped to zero.
        await ClearGlobalRunnerRegistryAsync();

        var runnerId = $"runner-div-{Guid.NewGuid():N}";
        await Grains.GetGrain<IRunnerGrain>(runnerId)
            .RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "div-host", "test-project"));
        await Grains.GetGrain<IRunnerGrain>(runnerId).UpdateAsync(5);

        var workflowA = $"wf-div-a-{Guid.NewGuid():N}";
        var workflowB = $"wf-div-b-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowA);
        await AssignActiveWorkForTestAsync(runnerId, workflowB);

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(_fixture.TimeProvider.GetUtcNow()));
        var capacity = await service.GetCapacityAsync("test-project");

        Assert.Equal(2, capacity.UsedSlots);
        Assert.Equal(5, capacity.TotalSlots);
    }
}

internal class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;
}
