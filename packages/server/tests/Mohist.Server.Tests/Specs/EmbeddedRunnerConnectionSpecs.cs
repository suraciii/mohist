using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Runner.Transport;
using Mohist.Server.Runner.Embedded;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class EmbeddedRunnerConnectionSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public EmbeddedRunnerConnectionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WorkflowHasAgentWork_EmbeddedConnectionPolls_CreatesRunnerWorkItemAndSession()
    {
        var runnerId = $"embedded-spec-{Guid.NewGuid():N}";
        var workflowId = $"wr_{Guid.NewGuid():N}";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var connection = new EmbeddedRunnerConnection(
            _fixture.Grains,
            scope.ServiceProvider.GetRequiredService<Sessions.AgentSessionService>(),
            scope.ServiceProvider.GetRequiredService<ILogger<EmbeddedRunnerConnection>>(),
            runnerId);

        await _fixture.Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.Key).ClearAsync();
        await connection.ConnectAsync(CancellationToken.None);
        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(AgentWorkflow(), new WorkflowCorrelationContext("project-1", "issue", "issue-1", 1));

        var work = await PollUntilWorkAsync(connection);

        Assert.Equal("mohist/agent", work.Uses);
        Assert.Equal("task", work.WorkType);
        Assert.Equal("plan", work.Stage);
        Assert.NotNull(work.Session);
        Assert.Equal(work.WorkId, work.Session.WorkId);

        await connection.ReportAsync(work, new WorkItemResult("completed", "ok"), CancellationToken.None);
        await connection.DisconnectAsync(CancellationToken.None);
    }

    private static async Task<WorkItem> PollUntilWorkAsync(EmbeddedRunnerConnection connection)
    {
        for (var i = 0; i < 100; i++)
        {
            var work = await connection.PollAsync(CancellationToken.None);
            if (work is not null) return work;
            await Task.Delay(20);
        }

        Assert.Fail("Embedded connection did not poll work");
        return default!;
    }

    private static WorkflowDefinitionInput AgentWorkflow() => new(
    [
        new StageDefinitionInput(
            "plan",
            [new TaskDefinitionInput("proposal", "Generate proposal", "mohist/agent", """{"stage":"plan","task":"proposal"}""")],
            [])
    ]);
}
