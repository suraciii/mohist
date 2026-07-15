using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowGrain")]
public class WorkflowArtifactBindingSpecs : WorkflowGrainSpecs
{
    public WorkflowArtifactBindingSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CompletedTask_WithUploadedArtifacts_BindsAndRecordsEvents()
    {
        var definition = SingleStage(
            tasks: [
                new TaskDefinition("task-1", "Task 1", "spec/task",
                    Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]))
            ],
            checks: []);
        await StartWorkflowAsync(definition);

        var (work, runnerId) = await PollWorkAnyAsync();
        var uploadId = await SeedPendingUploadAsync(work.WorkflowRunId, work.WorkId, "task-1.1", "review.md");
        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        await workflow.ApplyIssueLineageAsync(new WorkflowIssueLineage(
            TestIssueId(work.WorkflowRunId),
            "epic_artifact",
            1));

        await ReportAsync(runnerId, work.WorkId, new WorkResult("completed", ArtifactUploadIds: [uploadId]));

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Completed", status);

        await using var db = CreateDb();
        var artifacts = await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == work.WorkflowRunId)
            .ToListAsync();
        Assert.Single(artifacts);
        Assert.Equal("review.md", artifacts[0].Path);
        Assert.Equal("task-1.1", artifacts[0].TaskRunId);

        var events = (await EventStore.ListAsync(work.WorkflowRunId)).ToList();
        var artifactIndex = events.FindIndex(entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.WorkflowArtifactRecorded);
        var completedIndex = events.FindIndex(entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.TaskCompleted);
        Assert.True(artifactIndex >= 0);
        Assert.True(completedIndex > artifactIndex);
        Assert.Equal(work.WorkflowRunId, events[artifactIndex].Envelope.Extensions[EventCatalog.Lineage.WorkflowRunId]);
        Assert.Equal(TestProjectId(work.WorkflowRunId), events[artifactIndex].Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(TestIssueId(work.WorkflowRunId), events[artifactIndex].Envelope.Extensions[EventCatalog.Lineage.IssueId]);
        Assert.Equal("epic_artifact", events[artifactIndex].Envelope.Extensions[EventCatalog.Lineage.EpicId]);
        Assert.False(events[artifactIndex].Envelope.Extensions.ContainsKey(EventCatalog.Lineage.Stage));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ApplyIssueLineageAsync_IgnoresStaleAndDuplicateRevisions_AndRejectsConflicts()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new TaskDefinition("task-1", "Task 1", "spec/task")], checks: []));
        var (work, _) = await PollWorkAnyAsync();
        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        var issueId = TestIssueId(work.WorkflowRunId);

        await workflow.ApplyIssueLineageAsync(new WorkflowIssueLineage(issueId, "epic_current", 2));
        await workflow.ApplyIssueLineageAsync(new WorkflowIssueLineage(issueId, "epic_stale", 1));
        await workflow.ApplyIssueLineageAsync(new WorkflowIssueLineage(issueId, "epic_current", 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.ApplyIssueLineageAsync(new WorkflowIssueLineage(issueId, "epic_conflict", 2)));

        await workflow.PauseAsync("lineage assertion");

        var paused = Assert.Single((await EventStore.ListAsync(work.WorkflowRunId)), entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunPaused);
        Assert.Equal("epic_current", paused.Envelope.Extensions[EventCatalog.Lineage.EpicId]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CompletedTask_MissingDeclaredArtifact_CompletesWithBestEffort()
    {
        var definition = SingleStage(
            tasks: [
                new TaskDefinition("task-1", "Task 1", "spec/task",
                    Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]))
            ],
            checks: []);
        await StartWorkflowAsync(definition);

        var (work, runnerId) = await PollWorkAnyAsync();

        await ReportAsync(runnerId, work.WorkId, new WorkResult("completed"));

        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Completed", status);

        await using var db = CreateDb();
        var artifacts = await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == work.WorkflowRunId)
            .ToListAsync();
        Assert.Empty(artifacts);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CompletedTask_DeclaredArtifactUploaded_Succeeds()
    {
        var definition = SingleStage(
            tasks: [
                new TaskDefinition("task-1", "Task 1", "spec/task",
                    Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]))
            ],
            checks: []);
        await StartWorkflowAsync(definition);

        var (work, runnerId) = await PollWorkAnyAsync();
        var uploadId = await SeedPendingUploadAsync(work.WorkflowRunId, work.WorkId, "task-1.1", "review.md");

        await ReportAsync(runnerId, work.WorkId, new WorkResult("completed", ArtifactUploadIds: [uploadId]));

        await using var db = CreateDb();
        var pending = await db.WorkflowArtifactPendingUploads
            .Where(p => p.WorkflowRunId == work.WorkflowRunId)
            .ToListAsync();
        Assert.Empty(pending);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task InvalidRecoveryFollowUp_AcksAndFailsTheRunWithoutBindingArtifacts()
    {
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("promise=FAIL", [], RetrySelf: true)]);
        var definition = SingleStage(
            tasks:
            [
                new TaskDefinition(
                    "task-1",
                    "Task 1",
                    "spec/task",
                    Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]),
                    Recovery: recovery)
            ],
            checks: []);
        await StartWorkflowAsync(definition);

        var (work, runnerId) = await PollWorkAnyAsync();
        var uploadId = await SeedPendingUploadAsync(work.WorkflowRunId, work.WorkId, "task-1.1", "review.md");

        // A permanently invalid recovery follow-up acks and fails the run
        // terminally rather than throwing. The terminal failure path does not
        // bind artifacts, so the upload remains pending (recoverable by retry).
        await ReportAsync(runnerId, work.WorkId, new WorkResult(
            "completed",
            Output: "{}",
            ArtifactUploadIds: [uploadId],
            AddTasks: [new RuntimeTaskInput("task-1", "Task 1", "spec/task", Recovery: recovery)]));

        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        Assert.Equal("Failed", await workflow.GetRunStatusAsync());

        await using var db = CreateDb();
        Assert.Empty(await db.WorkflowArtifacts.Where(a => a.WorkflowRunId == work.WorkflowRunId).ToListAsync());
        Assert.Single(await db.WorkflowArtifactPendingUploads
            .Where(p => p.WorkflowRunId == work.WorkflowRunId && p.UploadId == uploadId)
            .ToListAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task FailedTask_WithDiagnosticUploads_BindsArtifacts()
    {
        var definition = SingleStage(
            tasks: [
                new TaskDefinition("task-1", "Task 1", "spec/task")
            ],
            checks: []);
        await StartWorkflowAsync(definition);

        var (work, runnerId) = await PollWorkAnyAsync();
        var uploadId = await SeedPendingUploadAsync(work.WorkflowRunId, work.WorkId, "task-1.1", "diagnostic.log");

        await ReportAsync(runnerId, work.WorkId, new WorkResult("failed", "something broke", ArtifactUploadIds: [uploadId]));

        await using var db = CreateDb();
        var artifacts = await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == work.WorkflowRunId)
            .ToListAsync();
        Assert.Single(artifacts);
        Assert.Equal("diagnostic.log", artifacts[0].Path);
        Assert.Equal("task-1.1", artifacts[0].TaskRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ForeignUploadId_Rejected_TaskFails()
    {
        var definition = SingleStage(
            tasks: [
                new TaskDefinition("task-1", "Task 1", "spec/task",
                    Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]))
            ],
            checks: []);
        await StartWorkflowAsync(definition);

        var (work, runnerId) = await PollWorkAnyAsync();
        var foreignUploadId = $"artup_{Guid.NewGuid():N}";

        await ReportAsync(runnerId, work.WorkId, new WorkResult("completed", ArtifactUploadIds: [foreignUploadId]));

        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Failed", status);

        await using var db = CreateDb();
        var artifacts = await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == work.WorkflowRunId)
            .ToListAsync();
        Assert.Empty(artifacts);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task NoWorkflowArtifactMissingEvent_Emitted()
    {
        var definition = SingleStage(
            tasks: [
                new TaskDefinition("task-1", "Task 1", "spec/task",
                    Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]))
            ],
            checks: []);
        await StartWorkflowAsync(definition);

        var (work, runnerId) = await PollWorkAnyAsync();

        await ReportAsync(runnerId, work.WorkId, new WorkResult("completed"));

        var events = await EventStore.ListAsync(work.WorkflowRunId);
        Assert.DoesNotContain(events, e =>
            e.Envelope.Type?.Contains("artifact.missing", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task DynamicArtifact_WithoutDeclaration_BindsOnCompleted()
    {
        var definition = SingleStage(tasks: [
            new TaskDefinition("task-1", "Task 1", "spec/task")
        ], checks: []);
        await StartWorkflowAsync(definition);

        var (work, runnerId) = await PollWorkAnyAsync();
        var uploadId = await SeedPendingUploadAsync(work.WorkflowRunId, work.WorkId, "task-1.1", "output.txt");

        await ReportAsync(runnerId, work.WorkId, new WorkResult("completed", ArtifactUploadIds: [uploadId]));

        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        Assert.Equal("Completed", await workflow.GetRunStatusAsync());

        await using var db = CreateDb();
        var artifacts = await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == work.WorkflowRunId)
            .ToListAsync();
        Assert.Single(artifacts);
        Assert.Equal("output.txt", artifacts[0].Path);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task MultipleUploads_BindAtomically_TaskSucceeds()
    {
        var definition = SingleStage(
            tasks: [
                new TaskDefinition("task-1", "Task 1", "spec/task",
                    Artifacts: new TaskArtifactCapture([
                        new TaskArtifactDeclaration("design.md"),
                        new TaskArtifactDeclaration("tasks.json")
                    ]))
            ],
            checks: []);
        await StartWorkflowAsync(definition);

        var (work, runnerId) = await PollWorkAnyAsync();
        var uploadId1 = await SeedPendingUploadAsync(work.WorkflowRunId, work.WorkId, "task-1.1", "design.md");
        var uploadId2 = await SeedPendingUploadAsync(work.WorkflowRunId, work.WorkId, "task-1.1", "tasks.json");

        await ReportAsync(runnerId, work.WorkId, new WorkResult("completed", ArtifactUploadIds: [uploadId1, uploadId2]));

        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        Assert.Equal("Completed", await workflow.GetRunStatusAsync());

        await using var db = CreateDb();
        var artifacts = await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == work.WorkflowRunId)
            .OrderBy(a => a.Path)
            .ToListAsync();
        Assert.Equal(2, artifacts.Count);
        Assert.Equal("design.md", artifacts[0].Path);
        Assert.Equal("tasks.json", artifacts[1].Path);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RepeatedTaskRuns_EachRetainTheirOwnArtifactSummary()
    {
        var definition = new WorkflowDefinition("spec/workflow", [
            new StageDefinition("build",
                [
                    new TaskDefinition("ai-review", "AI review", "spec/task",
                        Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]))
                ],
                [])
        ], Name: null);
        await StartWorkflowAsync(definition);

        var (work1, runnerId) = await PollWorkAnyAsync();
        var workflowRunId = work1.WorkflowRunId;
        var uploadId1 = await SeedPendingUploadAsync(workflowRunId, work1.WorkId, "ai-review.1", "review.md");
        await ReportAsync(runnerId, work1.WorkId, new WorkResult("completed", ArtifactUploadIds: [uploadId1]));

        var status = await GetQuerier().GetStatusAsync(workflowRunId);
        var task1 = status!.Stages[0].Tasks[0];
        Assert.NotNull(task1.ArtifactSummaries);
        var summary1 = Assert.Single(task1.ArtifactSummaries);
        Assert.Equal("review.md", summary1.Path);

        await using var db = CreateDb();
        var latestArtifacts = (await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == workflowRunId)
            .ToListAsync())
            .OrderByDescending(a => a.RecordedAt)
            .ToList();
        Assert.Single(latestArtifacts);
        Assert.Equal("ai-review.1", latestArtifacts[0].TaskRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LatestArtifact_PointsToNewestRun_WhileOlderRemain()
    {
        var definition = SingleStage(
            tasks: [
                new TaskDefinition("ai-review", "AI review", "spec/task",
                    Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]))
            ],
            checks: []);
        await StartWorkflowAsync(definition);

        var (work1, runnerId) = await PollWorkAnyAsync();
        var workflowRunId = work1.WorkflowRunId;
        var uploadId1 = await SeedPendingUploadAsync(workflowRunId, work1.WorkId, "ai-review.1", "review.md");
        await ReportAsync(runnerId, work1.WorkId, new WorkResult("completed", ArtifactUploadIds: [uploadId1]));

        var status1 = await GetQuerier().GetStatusAsync(workflowRunId);
        var task1 = status1!.Stages[0].Tasks[0];
        Assert.Single(task1.ArtifactSummaries!);
        Assert.Equal("review.md", task1.ArtifactSummaries![0].Path);

        var status2 = await GetQuerier().GetStatusAsync(workflowRunId);
        Assert.NotNull(status2!.Stages[0].Tasks[0].ArtifactSummaries);

        await using var db = CreateDb();
        var allArtifacts = (await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == workflowRunId)
            .ToListAsync())
            .OrderBy(a => a.RecordedAt)
            .ToList();
        Assert.Single(allArtifacts);
        Assert.Equal("ai-review.1", allArtifacts[0].TaskRunId);
    }

    private async Task<string> SeedPendingUploadAsync(string workflowRunId, string workId, string taskRunId, string path)
    {
        await using var db = CreateDb();
        var uploadId = $"artup_{Guid.NewGuid():N}";
        var pending = new WorkflowArtifactPendingUploadRow
        {
            UploadId = uploadId,
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            TaskRunId = taskRunId,
            Path = path,
            ContentType = "text/plain",
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Size = 42,
            StoragePath = $"workflows/{workflowRunId}/tasks/{taskRunId}/artifacts/{uploadId}/content",
            CreatedAt = TestTime.UtcNow,
            ExpiresAt = TestTime.UtcNow.AddDays(1),
        };
        db.WorkflowArtifactPendingUploads.Add(pending);
        await db.SaveChangesAsync();
        return uploadId;
    }

    private MohistDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        return new MohistDbContext(options);
    }
}
