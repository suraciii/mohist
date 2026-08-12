using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public sealed partial class WorkflowGrainStateSaveFailureSpecs
{
    [Fact]
    public async Task TaskReport_SaveFailure_ReplaysTheBoundUploadWithoutDuplicatingItsArtifact()
    {
        const string workflowRunId = "wr-artifact-bind-save-failure";
        const string projectId = "proj-artifact-bind-save-failure";
        const string workerId = "worker-artifact-bind-save-failure";
        const string uploadId = "artup_artifact_bind_save_failure";

        await SeedWorkflowTemplateAsync(projectId);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var setup = CreateGrain(scope.ServiceProvider, store, workflowRunId);
        await setup.OnActivateAsync(CancellationToken.None);
        await setup.EnsureStartedAsync(new WorkflowIssueContext(projectId, 1, null));
        await setup.AssignWorkerAsync(workerId);
        var work = Assert.IsType<WorkItem>(await setup.ClaimNextAsync(workerId));
        var taskRunId = Assert.Single((await store.LoadAsync(workflowRunId))!.CurrentStage().Tasks).Id;
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
            {
                UploadId = uploadId,
                WorkflowRunId = workflowRunId,
                WorkId = work.Id!,
                TaskRunId = taskRunId,
                Path = "result.txt",
                ContentType = "text/plain",
                ContentHash = "sha256:artifact-bind-save-failure",
                Size = 7,
                StoragePath = "workflows/test/result.txt",
                CreatedAt = FixedTime,
                ExpiresAt = FixedTime.AddDays(1),
            });
            await db.SaveChangesAsync();
        }

        var report = new TaskReport(
            work.Id!,
            TaskReportStatus.Succeeded,
            Output: null,
            Artifacts: null,
            ArtifactUploadIds: [uploadId],
            TaskRunId: taskRunId);
        var failingStore = new FailingWorkflowRunStore(store);
        var firstDelivery = CreateGrain(scope.ServiceProvider, failingStore, workflowRunId);
        await firstDelivery.OnActivateAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstDelivery.ReceiveTaskReportAsync(workerId, work.Id!, report));

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var artifact = Assert.Single(await db.WorkflowArtifacts
                .Where(row => row.WorkflowRunId == workflowRunId)
                .ToListAsync());
            Assert.Equal(uploadId, artifact.SourceUploadId);
            Assert.Equal(taskRunId, artifact.TaskRunId);
            Assert.Null(await db.WorkflowArtifactPendingUploads.FindAsync(uploadId));
        }
        Assert.Equal(TaskRunStatus.Running,
            Assert.Single((await store.LoadAsync(workflowRunId))!.CurrentStage().Tasks).Status);

        var replay = CreateGrain(scope.ServiceProvider, failingStore, workflowRunId);
        await replay.OnActivateAsync(CancellationToken.None);
        Assert.Equal(ReportAck.Accepted,
            await replay.ReceiveTaskReportAsync(workerId, work.Id!, report));

        Assert.Equal(TaskRunStatus.Completed,
            Assert.Single((await store.LoadAsync(workflowRunId))!.CurrentStage().Tasks).Status);
        await using var assertionDb = await dbFactory.CreateDbContextAsync();
        var replayedArtifact = Assert.Single(await assertionDb.WorkflowArtifacts
            .Where(row => row.WorkflowRunId == workflowRunId)
            .ToListAsync());
        Assert.Equal(uploadId, replayedArtifact.SourceUploadId);
        Assert.Equal(taskRunId, replayedArtifact.TaskRunId);
    }
}
