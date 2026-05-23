using Microsoft.Extensions.DependencyInjection;
using Mohist.Runner.Actions;
using Mohist.Runner.Transport;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class WorkResultNormalizationSpecs
{
    [Fact]
    public async Task SuccessfulTaskAction_BecomesCompleted()
    {
        var result = await ExecuteAsync("task", "success");

        Assert.Equal("completed", result.Status);
    }

    [Fact]
    public async Task FailedTaskAction_BecomesFailed()
    {
        var result = await ExecuteAsync("task", "failure");

        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task SuccessfulCheckAction_BecomesPass()
    {
        var result = await ExecuteAsync("check", "success");

        Assert.Equal("pass", result.Status);
    }

    [Fact]
    public async Task FailedCheckAction_BecomesFail()
    {
        var result = await ExecuteAsync("check", "failure");

        Assert.Equal("fail", result.Status);
    }

    [Fact]
    public async Task PendingCheckAction_StaysPending()
    {
        var result = await ExecuteAsync("check", "pending");

        Assert.Equal("pending", result.Status);
    }

    [Fact]
    public async Task SuccessfulLoadAction_BecomesLoaded()
    {
        var result = await ExecuteAsync("load", "success");

        Assert.Equal("loaded", result.Status);
    }

    [Fact]
    public async Task FailedLoadAction_BecomesFailed()
    {
        var result = await ExecuteAsync("load", "failure");

        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task UnknownAction_ReportsFailureForWorkType()
    {
        var executor = CreateExecutor(null);

        var result = await executor.ExecuteAsync(SpecHelpers.Work("check", "missing/action"), CancellationToken.None);

        Assert.Equal("fail", result.Status);
    }

    private static Task<WorkItemResult> ExecuteAsync(string workType, string actionStatus)
    {
        var executor = CreateExecutor(new FakeAction(actionStatus));
        return executor.ExecuteAsync(SpecHelpers.Work(workType), CancellationToken.None);
    }

    private static WorkExecutor CreateExecutor(IAction? action)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var manager = new ActionManager(services, SpecHelpers.Logger<ActionManager>());
        if (action is not null)
            manager.Register("spec/action", () => action);

        return new WorkExecutor(manager, SpecHelpers.Logger<WorkExecutor>(), SpecHelpers.CreateWorkspaceManager("/tmp/test"));
    }

    private sealed class FakeAction : IAction
    {
        private readonly string _status;

        public FakeAction(string status)
        {
            _status = status;
        }

        public Task<ActionResult> ExecuteAsync(ActionContext context)
        {
            return Task.FromResult(new ActionResult(_status, "ok", "{}"));
        }
    }
}
