using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workspace.Grains;
using Mohist.Workflow.Definition;

namespace Mohist.Server.OrleansTests.Support;

public sealed class OrleansL0WorkflowGrainFixture : WorkflowGrainFixture
{
    public const string WarmupRunnerId = "orleans-l0-fixture-warmup";
    public const string RecoveryWorkflowId = "orleans-l0-recovery-continuation";
    public const string RecoveryRunnerId = "orleans-l0-recovery-runner";
    public const string RecoveryProjectId = "orleans-l0-recovery-project";

    public static RecoveryDefinition RecoveryDefinition { get; } = new(
        2,
        [new RecoveryHandlerDefinition("output.promise=FAIL", [], RetrySelf: true)]);

    public string RecoveryFreshWorkId { get; private set; } = string.Empty;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Pay Orleans serializer, first activation, and reset cost during
        // fixture setup so the first business Spec measures its own claim.
        var runner = Grains.GetGrain<IRunnerGrain>(WarmupRunnerId);
        await runner.RegisterAsync(new RunnerInfo(
            WarmupRunnerId,
            ["spec/*"],
            "test-host",
            null));
        _ = await runner.GetInfoAsync();
        await runner.UnregisterAsync();

        var workspaceProjectId = SeedWorkspaceProject();
        var workspace = Grains.GetGrain<IWorkspaceGrain>(
            GrainKey.Workspace(workspaceProjectId, "workspace-warmup"));
        await workspace.CreateManualAsync(
            "workspace-warmup",
            ["server"],
            TimeProvider.GetUtcNow());

        // Template seeding, first activation, and generated copier JIT are
        // fixture costs. Warm the same report/claim path once, then prepare the
        // starting claim measured by the business Spec.
        var warmupWorkflowId = $"{RecoveryWorkflowId}-warmup";
        var warmupRunnerId = $"{RecoveryRunnerId}-warmup";
        var warmupWorkId = await PrepareRecoveryWorkflowAsync(
            warmupWorkflowId,
            warmupRunnerId,
            $"{RecoveryProjectId}-warmup");
        var warmupWorkflow = Grains.GetGrain<IWorkflowGrain>(warmupWorkflowId);
        var warmupAck = await warmupWorkflow.ReceiveTaskReportAsync(
            warmupRunnerId,
            warmupWorkId,
            RecoveryReport(warmupWorkId));
        if (warmupAck != ReportAck.Accepted || await warmupWorkflow.ClaimNextAsync(warmupRunnerId) is null)
            throw new InvalidOperationException("Recovery continuation warmup failed");

        RecoveryFreshWorkId = await PrepareRecoveryWorkflowAsync(
            RecoveryWorkflowId,
            RecoveryRunnerId,
            RecoveryProjectId);
    }

    public string SeedWorkspaceProject()
    {
        var projectId = $"wgs-{Guid.NewGuid():N}";
        using var db = GrainTestConfig.CreateDbContext(ConnectionString);
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            RepositoriesJson = """
                [
                  {"name":"server","gitUrl":"https://git.test/server.git","baseBranch":"main","isDefault":true},
                  {"name":"web","gitUrl":"https://git.test/web.git","baseBranch":"main","isDefault":false}
                ]
                """,
            CreatedAt = TimeProvider.GetUtcNow(),
            UpdatedAt = TimeProvider.GetUtcNow(),
        });
        db.SaveChanges();
        return projectId;
    }

    public static RuntimeTaskInput RecoveryFollowUp() => new(
        "review",
        "Review",
        "spec/review",
        With: JsonSerializer.SerializeToElement(new { options = "${{ vars.agent }}" }),
        Recovery: RecoveryDefinition,
        RecoveryRemaining: 1,
        Expect: JsonSerializer.SerializeToElement(new
        {
            markers = new[] { new { path = "review.md", failIf = "${{ vars.marker }}" } },
        }));

    private static TaskReport RecoveryReport(string workId) => new(
        workId,
        TaskReportStatus.Succeeded,
        Output: null,
        Artifacts: null,
        Detail: null,
        AddTasks: new List<RuntimeTaskInput> { RecoveryFollowUp() },
        TaskRunId: workId);

    private async Task<string> PrepareRecoveryWorkflowAsync(
        string workflowId,
        string runnerId,
        string projectId)
    {
        var definition = new WorkflowDefinition(
        [
            new StageDefinition(
                "build",
                [new TaskDefinition("review", "Review", "spec/review", Recovery: RecoveryDefinition)],
                [])
        ]);
        await WorkflowGrainTestHelpers.SeedWorkflowTemplateAsync(
            ConnectionString,
            workflowId,
            definition,
            projectId);
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: DateTimeOffset.UnixEpoch,
            ProjectId: projectId,
            IssueNumber: 1)));
        var assignment = await workflow.AssignWorkerAsync(runnerId);
        if (assignment.Status != WorkflowAssignmentStatus.Assigned)
            throw new InvalidOperationException($"Recovery workflow assignment failed: {assignment.Reason}");
        var fresh = await workflow.ClaimNextAsync(runnerId)
            ?? throw new InvalidOperationException("Recovery workflow did not expose its first claim");
        return fresh.Id
            ?? throw new InvalidOperationException("Recovery workflow claim did not carry a work id");
    }
}
