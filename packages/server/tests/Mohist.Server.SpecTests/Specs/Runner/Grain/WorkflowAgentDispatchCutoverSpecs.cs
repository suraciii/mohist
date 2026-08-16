using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("WorkflowGrain")]
public sealed class WorkflowAgentDispatchCutoverSpecs : Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainSpecs
{
    public WorkflowAgentDispatchCutoverSpecs(
        Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task AgentTask_ActivatesAndClaimsAgentJob_ThenFinalizesWorkflowEffects()
    {
        await ClearBacklogAsync();
        var workflowId = $"workflow-agent-cutover-{Guid.NewGuid():N}";
        var projectId = TestProjectId(workflowId);
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"runner-agent-cutover-{Guid.NewGuid():N}");
        _workflowId = workflowId;
        _runnerId = runnerId;
        var agentId = $"agent_{Guid.NewGuid():N}";
        await SeedActiveAgentAsync(projectId, agentId, "reviewer");

        var expect = With("""{"markers":[{"path":"_output","oneOf":["<promise>done</promise>"]}]}""");
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(
            workflowId,
            SingleStage(
                tasks:
                [
                    new TaskDefinition(
                        "review",
                        "Review",
                        "mohist/agent",
                        With("""{"name":"reviewer","prompt":"Review the change.","session":"review","timeout":120000}"""),
                        expect,
                        new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]),
                        new Dictionary<string, string> { ["reviewResult"] = "output.promise" })
                ],
                checks: []),
            projectId);
        await workflow.StartAsync(TestInput(projectId));

        var dispatch = ResolveDispatchService();
        var readiness = new RunnerPollRequest(
            [],
            [],
            [new RuntimeReadinessWitness("opencode", true, 1)],
            ConnectionGeneration: "cutover-connection",
            AdmissionReady: true);

        // The workflow claim creates and activates the durable handoff. The
        // candidate list was built before activation, so this poll cannot
        // accidentally claim the newly-created AgentJob as a second dispatch.
        Assert.Empty((await dispatch.PollAsync(runnerId, readiness)).Dispatches);

        var running = await LoadRunAsync(workflowId);
        var task = Assert.Single(running.CurrentStage().Tasks);
        var handoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(
            WorkflowAgentHandoffCodec.KeyFor(projectId, workflowId, task.Id, task.WorkId!));
        var handoffPlan = await handoff.GetPlanAsync();
        Assert.True(
            task.AgentInvocation is not null,
            $"status={task.Status}; worker={task.WorkerId}; work={task.WorkId}; workflow={running.Status}; error={task.Error?.Code}; handoff={handoffPlan?.Disposition}");
        var link = task.AgentInvocation!;
        Assert.Null(await Services.GetRequiredService<IDispatchSnapshotStore>()
            .LoadJsonAsync(workflowId, task.WorkId!));
        Assert.Equal(WorkflowAgentHandoffDisposition.Activated, handoffPlan!.Disposition);

        using (var scope = Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
            var session = await sessions.LoadAsync(link!.SessionId);
            Assert.NotNull(session);
            Assert.Equal(workflowId, session!.Metadata.Label(AgentSessionQueryMetadataKeys.WorkflowRunId));
            Assert.Equal(task.Id, session.Metadata.Label(AgentSessionQueryMetadataKeys.TaskRunId));
            Assert.Equal(link.InvocationId, session.Metadata.Label(AgentSessionQueryMetadataKeys.InvocationId));
            Assert.Equal("review", session.Metadata.Label(AgentSessionQueryMetadataKeys.SessionName));
        }

        var claimed = Assert.Single((await dispatch.PollAsync(runnerId, readiness)).Dispatches);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, claimed.OwnerKind);
        Assert.Equal(link.JobId, claimed.AgentJobId);
        Assert.Equal(workflowId, claimed.WorkflowRunId);
        Assert.Equal(task.Id, claimed.TaskRunId);
        Assert.Equal(JSON.Serialize(expect), claimed.Expect);
        Assert.Null(claimed.Uses);

        var runtime = await Grains.GetGrain<IRunnerGrain>(runnerId).GetRuntimeStateAsync();
        var active = Assert.Single(runtime.ActiveWorks);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, active.OwnerKind);
        Assert.Equal(link.JobId, active.OwnerId);
        Assert.DoesNotContain(runtime.ActiveWorks, work => work.OwnerKind == WorkDispatchOwnerKinds.Workflow);

        await SeedPendingUploadAsync(workflowId, link.WorkId, link.TaskRunId, "upload-review", "review.md");
        var output = JSON.DeserializeElement("""
            {"promise":"done","expectation":{"satisfied":true,"matched":"<promise>done</promise>","message":"matched"}}
            """);
        await Grains.GetGrain<IAgentJobGrain>(link.JobId)
            .ReportResultAsync(runnerId, claimed.WorkId, new WorkResult(
                "completed",
                Output: output,
                ArtifactUploadIds: ["upload-review"]));

        using (var scope = Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventDispatcherService>()
                .DispatchAsync(CancellationToken.None);
        }

        await TestWait.ForAsync(
            () => LoadRunAsync(workflowId),
            run => Assert.Single(run.CurrentStage().Tasks).Status == TaskRunStatus.Completed,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(25),
            "workflow Agent invocation to finalize");

        var finalized = await LoadRunAsync(workflowId);
        var completed = Assert.Single(finalized.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, completed.Status);
        Assert.Equal("done", completed.Output!.Value.GetProperty("promise").GetString());
        Assert.True(completed.AgentInvocationSettlement!.IsSettled);
        var variables = await Services.GetRequiredService<WorkflowRunVariablesStore>()
            .GetVariablesAsync(workflowId);
        Assert.Equal("done", variables.Vars!.Value.GetProperty("reviewResult").GetString());

        await using var db = await Services.GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var artifact = await db.WorkflowArtifacts.SingleAsync(row => row.WorkflowRunId == workflowId);
        Assert.Equal("review.md", artifact.Path);
    }

    private DispatchService ResolveDispatchService() =>
        Services.GetRequiredService<DispatchService>();

    private async Task SeedActiveAgentAsync(string projectId, string agentId, string name)
    {
        var now = TestTime.UtcNow;
        var agent = new Mohist.Server.Agent.Domain.Agent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = name,
            Description = "Workflow test agent",
            Instructions = "Review carefully.",
            AgentConfig = JsonSerializer.SerializeToElement(new { runtime = "opencode" }),
            Skills = [],
            Status = AgentStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var db = await Services.GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = name,
            Status = AgentStatus.Active,
            State = AgentStore.Serialize(agent),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedPendingUploadAsync(
        string workflowId,
        string workId,
        string taskRunId,
        string uploadId,
        string path)
    {
        await using var db = await Services.GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
        {
            UploadId = uploadId,
            WorkflowRunId = workflowId,
            WorkId = workId,
            TaskRunId = taskRunId,
            Path = path,
            Kind = "file",
            Size = 4,
            ContentType = "text/markdown",
            CreatedAt = TestTime.UtcNow,
            ExpiresAt = TestTime.UtcNow.AddHours(1),
            StoragePath = "/tmp/review.md",
        });
        await db.SaveChangesAsync();
    }
}
