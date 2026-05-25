using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Runner;
using Mohist.Runner.Actions;
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

    [Fact]
    public async Task WorkflowHasAgentAndArtifactCheck_SharedEmbeddedRunnerExecutes_WorkflowReachesApproval()
    {
        var runnerId = $"embedded-host-{Guid.NewGuid():N}";
        var workflowId = $"wr_{Guid.NewGuid():N}";
        var projectRoot = Path.Combine(Path.GetTempPath(), $"mohist-embedded-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        await using var scope = _fixture.Services.CreateAsyncScope();

        var connection = new EmbeddedRunnerConnection(
            _fixture.Grains,
            scope.ServiceProvider.GetRequiredService<Sessions.AgentSessionService>(),
            scope.ServiceProvider.GetRequiredService<ILogger<EmbeddedRunnerConnection>>(),
            runnerId);
        var runnerServices = RunnerServices(projectRoot);
        var actionManager = new ActionManager(runnerServices, runnerServices.GetRequiredService<ILogger<ActionManager>>());
        RunnerActionCatalog.RegisterDefaults(actionManager, runnerServices);
        var executor = new WorkExecutor(
            actionManager,
            scope.ServiceProvider.GetRequiredService<ILogger<WorkExecutor>>(),
            new FixedWorkspaceManager(projectRoot));
        var host = new RunnerHost(
            connection,
            executor,
            scope.ServiceProvider.GetRequiredService<ILogger<RunnerHost>>(),
            TimeProvider.System,
            new RunnerHostOptions { IdleDelay = TimeSpan.FromMilliseconds(10), HeartbeatInterval = TimeSpan.FromHours(1) });

        await _fixture.Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.Key).ClearAsync();
        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(AgentAndArtifactWorkflow(), new WorkflowCorrelationContext("project-1", "issue", "issue-1", 1), new WorkflowStartInput(Variables(projectRoot)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = host.RunAsync(cts.Token);
        await WaitUntilAsync(async () =>
        {
            var status = await workflow.GetStatusAsync();
            return status?.Status == "AwaitingApproval" && status.CurrentStage == "plan";
        });
        await cts.CancelAsync();
        await runTask;

        Assert.True(File.Exists(Path.Combine(projectRoot, "openspec/changes/issue-1/proposal.md")));
        var finalStatus = await workflow.GetStatusAsync();
        Assert.NotNull(finalStatus);
        Assert.Equal("AwaitingApproval", finalStatus.Status);
        Assert.Contains(finalStatus.Stages.Single(s => s.Stage == "plan").Checks, c => c.Name == "proposal-complete" && c.Status == "Passed");
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail("Condition was not met");
    }

    private static WorkflowDefinitionInput AgentWorkflow() => new(
    [
        new StageDefinitionInput(
            "plan",
            [new TaskDefinitionInput("proposal", "Generate proposal", "mohist/agent", """{"stage":"plan","task":"proposal"}""")],
            [])
    ]);

    private static WorkflowDefinitionInput AgentAndArtifactWorkflow() => new(
    [
        new StageDefinitionInput(
            "plan",
            [new TaskDefinitionInput("proposal", "Generate proposal", "mohist/agent", """{"stage":"plan","task":"proposal","requireFiles":[{"path":"${{ openspecChangeDir }}/proposal.md"}],"requireMarkers":[{"path":"${{ openspecChangeDir }}/proposal.md","marker":"<mohist:proposal>PASS</mohist:proposal>"}]}""")],
            [new CheckDefinitionInput("proposal-complete", "Proposal complete", "core/artifact-exists", """{"path":"${{ openspecChangeDir }}/proposal.md"}""")],
            RequiresApproval: true)
    ]);

    private static string Variables(string projectRoot) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["project"] = new { id = "project-1", name = "Project", path = projectRoot, baseBranch = "main" },
        ["issue"] = new { id = "issue-1", number = 1, title = "Smoke", body = "body" },
        ["openspecChangeName"] = "issue-1",
        ["openspecChangeDir"] = "openspec/changes/issue-1",
        ["model"] = new { @default = "", stage = new Dictionary<string, string>() },
    });

    private sealed class FixedWorkspaceManager : IWorkspaceManager
    {
        private readonly string _path;

        public FixedWorkspaceManager(string path)
        {
            _path = path;
        }

        public Task<WorkspaceInfo> EnsureAsync(Dictionary<string, System.Text.Json.JsonElement?> variables, CancellationToken ct)
        {
            Directory.CreateDirectory(_path);
            return Task.FromResult(new WorkspaceInfo(_path, null, Path.Combine(_path, "openspec/changes/issue-1")));
        }
    }

    private static ServiceProvider RunnerServices(string projectRoot)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAgentExecutor>(new FakeAgentExecutor(projectRoot));
        services.AddSingleton<ISessionTelemetrySink, NullSessionTelemetrySink>();
        services.AddSingleton<IAgentCompletionVerifier, AgentCompletionVerifier>();
        services.AddSingleton<IAgentSessionRepairer, NoopAgentSessionRepairer>();
        return services.BuildServiceProvider();
    }

    private sealed class FakeAgentExecutor : IAgentExecutor
    {
        private readonly string _projectRoot;

        public FakeAgentExecutor(string projectRoot)
        {
            _projectRoot = projectRoot;
        }

        public async Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request)
        {
            var proposalPath = Path.Combine(_projectRoot, "openspec/changes/issue-1/proposal.md");
            Directory.CreateDirectory(Path.GetDirectoryName(proposalPath)!);
            await File.WriteAllTextAsync(proposalPath, "# Proposal\n\nGenerated by fake agent.\n<mohist:proposal>PASS</mohist:proposal>\n", request.CancellationToken);

            return new AgentExecutionResult(0, "ok");
        }
    }
}
