using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.SignalR;

public class RunnerWorkflowStatusRouterSpecs
{
    private const string WorkflowRunId = "wf-router-1";
    private const string RunnerId = "runner-router-1";

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RouteAsync_RunnerIsConnected_PushesReceiveWorkflowRunStatus()
    {
        var tracker = new RunnerConnectionTracker();
        tracker.Register(RunnerId, "conn-1");

        var hub = new RecordingRunnerHubContext();
        var workflow = new StubWorkflowGrain { AssignedWorkerId = RunnerId, Status = WorkflowRunStatus.Completed };
        var grains = new StubGrainFactory(workflow);

        var router = new RunnerWorkflowStatusRouter(hub, tracker, grains, NullLogger<RunnerWorkflowStatusRouter>.Instance);

        await router.RouteAsync(WorkflowRunId, WorkflowRunStatus.Completed);

        var message = Assert.Single(hub.SentMessages);
        Assert.Equal("conn-1", message.ConnectionId);
        Assert.Equal("ReceiveWorkflowRunStatus", message.Method);
        var payload = Assert.IsType<WorkflowRunStatusNotification>(Assert.Single(message.Arguments));
        Assert.Equal(WorkflowRunId, payload.WorkflowRunId);
        Assert.Equal("Completed", payload.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RouteAsync_RunnerIsOffline_NoPushAndBackstopIsReliedOn()
    {
        var tracker = new RunnerConnectionTracker();
        var hub = new RecordingRunnerHubContext();
        var workflow = new StubWorkflowGrain { AssignedWorkerId = RunnerId, Status = WorkflowRunStatus.Stopped };
        var grains = new StubGrainFactory(workflow);

        var router = new RunnerWorkflowStatusRouter(hub, tracker, grains, NullLogger<RunnerWorkflowStatusRouter>.Instance);

        await router.RouteAsync(WorkflowRunId, WorkflowRunStatus.Stopped);

        Assert.Empty(hub.SentMessages);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RouteAsync_NoAssignedRunner_DropsNotificationWithoutPushing()
    {
        var tracker = new RunnerConnectionTracker();
        tracker.Register(RunnerId, "conn-1");
        var hub = new RecordingRunnerHubContext();
        var workflow = new StubWorkflowGrain { AssignedWorkerId = null, Status = WorkflowRunStatus.Completed };
        var grains = new StubGrainFactory(workflow);

        var router = new RunnerWorkflowStatusRouter(hub, tracker, grains, NullLogger<RunnerWorkflowStatusRouter>.Instance);

        await router.RouteAsync(WorkflowRunId, WorkflowRunStatus.Completed);

        Assert.Empty(hub.SentMessages);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RouteAsync_FailedStatus_PushesFailedStatusName()
    {
        var tracker = new RunnerConnectionTracker();
        tracker.Register(RunnerId, "conn-failed");
        var hub = new RecordingRunnerHubContext();
        var workflow = new StubWorkflowGrain { AssignedWorkerId = RunnerId, Status = WorkflowRunStatus.Failed };
        var grains = new StubGrainFactory(workflow);

        var router = new RunnerWorkflowStatusRouter(hub, tracker, grains, NullLogger<RunnerWorkflowStatusRouter>.Instance);

        await router.RouteAsync(WorkflowRunId, WorkflowRunStatus.Failed);

        var message = Assert.Single(hub.SentMessages);
        var payload = Assert.IsType<WorkflowRunStatusNotification>(Assert.Single(message.Arguments));
        Assert.Equal("Failed", payload.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RouteAsync_HubThrows_DoesNotPropagate()
    {
        var tracker = new RunnerConnectionTracker();
        tracker.Register(RunnerId, "conn-throws");
        var hub = new ThrowingHubContext();
        var workflow = new StubWorkflowGrain { AssignedWorkerId = RunnerId, Status = WorkflowRunStatus.Completed };
        var grains = new StubGrainFactory(workflow);

        var router = new RunnerWorkflowStatusRouter(hub, tracker, grains, NullLogger<RunnerWorkflowStatusRouter>.Instance);

        await router.RouteAsync(WorkflowRunId, WorkflowRunStatus.Completed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RouteAsync_EmptyRunId_NoOp()
    {
        var tracker = new RunnerConnectionTracker();
        tracker.Register(RunnerId, "conn-1");
        var hub = new RecordingRunnerHubContext();
        var grains = new StubGrainFactory(new StubWorkflowGrain());

        var router = new RunnerWorkflowStatusRouter(hub, tracker, grains, NullLogger<RunnerWorkflowStatusRouter>.Instance);

        await router.RouteAsync(string.Empty, WorkflowRunStatus.Completed);

        Assert.Empty(hub.SentMessages);
    }

    private sealed class StubWorkflowGrain : IWorkflowGrain
    {
        public string? AssignedWorkerId { get; set; }
        public WorkflowRunStatus Status { get; set; }

        public Task<string?> GetAssignedWorkerIdAsync() => Task.FromResult(AssignedWorkerId);
        public Task<string?> GetRunStatusAsync() => Task.FromResult<string?>(Status.ToString());

        // Unused members — stubbed because the router only calls these two.
        public Task StartAsync(WorkflowStartInput? input = null) => Task.CompletedTask;
        public Task ActivateAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task PauseAsync(string? reason = null) => Task.CompletedTask;
        public Task StopAsync(string? reason = null) => Task.CompletedTask;
        public Task ApproveAsync() => Task.CompletedTask;
        public Task<string> RequestChangesAsync(string body) => Task.FromResult(string.Empty);
        public Task RetryAsync() => Task.CompletedTask;
        public Task RerunAsync() => Task.CompletedTask;
        public Task<WorkflowControlResult> RerunFromStageAsync(string stageId) =>
            Task.FromResult(WorkflowControlResult.Ok());
        public Task<RuntimeTaskAddedResult> AddTaskAsync(RuntimeTaskInput task) =>
            Task.FromResult(new RuntimeTaskAddedResult(string.Empty, string.Empty, string.Empty));
        public Task<AddTasksBatchResult> AddTasksAsync(AddTasksBatchRequest request) =>
            Task.FromResult(new AddTasksBatchResult(string.Empty, string.Empty, 0));
        public Task<bool> HasIncompleteTaskWithUsesAsync(string uses) => Task.FromResult(false);
        public Task<bool> HasIncompleteTaskByIdAsync(string id) => Task.FromResult(false);
        public Task<Mohist.Server.Workflow.Grains.WorkflowAssignmentResult> AssignWorkerAsync(string workerId) =>
            Task.FromResult(new Mohist.Server.Workflow.Grains.WorkflowAssignmentResult(WorkflowAssignmentStatus.Assigned));
        public Task<WorkItem?> ClaimNextAsync(string workerId) =>
            Task.FromResult<WorkItem?>(null);
        public Task<Mohist.Server.Workflow.Grains.ReportAck> FailActiveWorkAsync(string workerId, string message)
            => Task.FromResult(Mohist.Server.Workflow.Grains.ReportAck.Stale);
        public Task<Mohist.Server.Workflow.Grains.ReportAck> ReceiveTaskReportAsync(string workerId, string workId, TaskReport report)
            => Task.FromResult(Mohist.Server.Workflow.Grains.ReportAck.Stale);
        public Task<Mohist.Server.Workflow.Grains.ReportAck> ReceiveCheckReportAsync(string workerId, string workId, CheckReport report)
            => Task.FromResult(Mohist.Server.Workflow.Grains.ReportAck.Stale);
        public Task ReleaseStageLocksAsync(string stage, string reason) => Task.CompletedTask;
        public Task<bool> IsStoppedOrTerminalAsync() => Task.FromResult(true);
        public Task<string?> GetCurrentWorkIdAsync() => Task.FromResult<string?>(null);
        public Task<Mohist.Server.Workflow.Grains.WorkflowActiveWorkView?> GetActiveWorkAsync(string workId) =>
            Task.FromResult<Mohist.Server.Workflow.Grains.WorkflowActiveWorkView?>(null);
        public Task<Mohist.Server.Workflow.Grains.WorkflowFeedbackRecord?> GetFeedbackAsync(string feedbackId) =>
            Task.FromResult<Mohist.Server.Workflow.Grains.WorkflowFeedbackRecord?>(null);
        public Task<IReadOnlyList<Mohist.Server.Workflow.Grains.WorkflowFeedbackRecord>> ListFeedbackAsync() =>
            Task.FromResult<IReadOnlyList<Mohist.Server.Workflow.Grains.WorkflowFeedbackRecord>>(Array.Empty<Mohist.Server.Workflow.Grains.WorkflowFeedbackRecord>());
        public Task DeactivateForTestAsync() => Task.CompletedTask;
    }

    private sealed class StubGrainFactory : IGrainFactory
    {
        private readonly IWorkflowGrain _workflow;
        public StubGrainFactory(IWorkflowGrain workflow) { _workflow = workflow; }

        public IWorkflowGrain GetWorkflowGrain() => _workflow;

        public TGrainInterface GetGrain<TGrainInterface>(Guid grainPrimaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey =>
            throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long grainPrimaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey =>
            throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(string grainPrimaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IWorkflowGrain))
                return (TGrainInterface)(object)_workflow;
            throw new NotSupportedException();
        }
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey =>
            throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey =>
            throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable =>
            throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid grainPrimaryKey, string? grainClassNamePrefix) => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long grainPrimaryKey, string? grainClassNamePrefix) => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string grainPrimaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IWorkflowGrain))
                return (TGrainInterface)(object)_workflow;
            throw new NotSupportedException();
        }
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix) => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix) => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId) => throw new NotSupportedException();
        Orleans.Runtime.IAddressable IGrainFactory.GetGrain(GrainId grainId) => throw new NotSupportedException();
        Orleans.Runtime.IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        Orleans.Runtime.IAddressable IGrainFactory.GetGrain(Type grainInterfaceType, IdSpan grainKey, string? grainClassNamePrefix) => throw new NotSupportedException();
        Orleans.Runtime.IAddressable IGrainFactory.GetGrain(Type grainInterfaceType, IdSpan grainKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) => throw new NotSupportedException();
        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) => throw new NotSupportedException();
    }

    private sealed class ThrowingHubContext : IHubContext<RunnerHub>
    {
        public IHubClients Clients => new ThrowingClients();
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class ThrowingClients : IHubClients
        {
            public IClientProxy All => throw new InvalidOperationException("all unreachable");
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new InvalidOperationException("all-except unreachable");
            public IClientProxy Client(string connectionId) => new ThrowingClientProxy();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new InvalidOperationException("clients unreachable");
            public IClientProxy Group(string groupName) => throw new InvalidOperationException("group unreachable");
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new InvalidOperationException("group-except unreachable");
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new InvalidOperationException("groups unreachable");
            public IClientProxy User(string userId) => throw new InvalidOperationException("user unreachable");
            public IClientProxy Users(IReadOnlyList<string> userIds) => throw new InvalidOperationException("users unreachable");
        }

        private sealed class ThrowingClientProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("signalR transport failure (test simulation)");
        }
    }
}
