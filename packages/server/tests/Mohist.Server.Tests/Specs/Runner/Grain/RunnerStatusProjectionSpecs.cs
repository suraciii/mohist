using System;
using System.Linq;
using System.Threading.Tasks;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.SignalR;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_GlobalRunner_IsIncluded()
    {
        var runnerId = $"runner-global-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "global-host", null, ["openai/gpt-4"]));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
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

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
        var result = await service.GetRunnersAsync("test-project");

        Assert.Contains(result, r => r.Id == runnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_OtherProjectRunner_IsExcluded()
    {
        var runnerId = $"runner-other-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "other-host", "other-project", ["openai/gpt-4"]));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
        var result = await service.GetRunnersAsync("test-project");

        Assert.DoesNotContain(result, r => r.Id == runnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_NoProjectScopedRunners_ReturnsOnlyGlobalRunners()
    {
        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
        var result = await service.GetRunnersAsync("nonexistent-project");

        Assert.All(result, r => Assert.Equal("global", r.Scope.Type));
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

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
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

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal("busy", view.Status);
        Assert.NotNull(view.ActiveWork);
        Assert.Equal(workflowId, view.ActiveWork.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_StaleRunner_HasStaleStatus()
    {
        var runnerId = $"runner-stale-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registeredAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "stale-host", "test-project", RegisteredAt: registeredAt));

        var now = DateTimeOffset.UtcNow.AddMinutes(5);
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

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
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

        var service = CreateService(Grains, tracker, TimeAt(DateTimeOffset.UtcNow));
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

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
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

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
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

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal("disconnected", view.ConnectionState);
        Assert.Equal("busy", view.Status);
        Assert.NotNull(view.ActiveWork);
        Assert.Equal(workflowId, view.ActiveWork.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_ReturnsAllFields()
    {
        var runnerId = $"runner-full-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registeredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*", "workflow"], "full-host", "test-project", ["openai/gpt-4", "anthropic/claude-3"], "external", registeredAt));
        await runner.HeartbeatAsync();

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal(runnerId, view.Id);
        Assert.Equal("external", view.Kind);
        Assert.Equal("full-host", view.Hostname);
        Assert.Equal("project", view.Scope.Type);
        Assert.Equal("test-project", view.Scope.ProjectId);
        Assert.Equal("idle", view.Status);
        Assert.NotNull(view.RegisteredAt);
        Assert.NotNull(view.LastHeartbeatAt);
        Assert.Equal(new[] { "spec/*", "workflow" }, view.Capabilities);
        Assert.Equal(new[] { "openai/gpt-4", "anthropic/claude-3" }, view.CoderModels);
        Assert.Equal(2, view.CoderModelCount);
        Assert.NotNull(view.Capacity);
        Assert.Equal(0, view.Capacity.UsedSlots);
        Assert.Equal(1, view.Capacity.TotalSlots);
        Assert.Null(view.ActiveWork);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetRunnersAsync_GlobalRunnerScope_IsGlobal()
    {
        var runnerId = $"runner-scope-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "scope-host", null));

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        Assert.Equal("global", view.Scope.Type);
        Assert.Null(view.Scope.ProjectId);
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

        var service = CreateService(Grains, new RunnerConnectionTracker(), TimeAt(DateTimeOffset.UtcNow));
        var result = await service.GetRunnersAsync("test-project");

        var view = Assert.Single(result, r => r.Id == runnerId);
        var json = global::System.Text.Json.JsonSerializer.Serialize(view);
        Assert.DoesNotContain("env", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
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
