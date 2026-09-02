using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.GrainContracts;

/// <summary>
/// End-to-end approval feedback regression coverage on the real WorkflowGrain.
/// The persisted event stream is the subscriber-equivalent observation: a
/// command commits one complete batch, so no reader can observe an approval
/// point for the rejected attempt between feedback resolution and rerun.
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainFeedbackRerunSpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainFeedbackRerunSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PlanApprovalFeedback_RerunsAllTasksAndDeclaredChecksBeforeApproval()
    {
        await AssertFeedbackRerunAsync(
            "wr-feedback-rerun-plan",
            new StageDefinition(
                "plan",
                [
                    new("draft", "Draft", "spec/plan"),
                    new("review", "Review plan", "spec/plan-review"),
                ],
                [
                    new("plan-repository-check", "Plan repository check", "spec/repository-check"),
                    new("plan-policy-check", "Plan policy check", "spec/policy-check"),
                ],
                RequiresApproval: true),
            ["draft", "review"],
            ["plan-repository-check", "plan-policy-check"]);
    }

    [Fact]
    public async Task CheckApprovalFeedback_RerunsReviewSynchronizationPublicationAndRepositoryChecks()
    {
        await AssertFeedbackRerunAsync(
            "wr-feedback-rerun-check",
            new StageDefinition(
                "check",
                [
                    new("review", "Review", "spec/review"),
                    new("synchronize", "Synchronize", "spec/synchronize"),
                    new("publish", "Publish", "spec/publish"),
                ],
                [
                    new("repository-clean", "Repository clean", "spec/repository-check"),
                    new("repository-policy", "Repository policy", "spec/policy-check"),
                ],
                RequiresApproval: true),
            ["review", "synchronize", "publish"],
            ["repository-clean", "repository-policy"]);
    }

    [Fact]
    public async Task ApprovalFeedbackFailure_LeavesOpenFeedbackAndDoesNotStartRerun()
    {
        var arrangement = await ArrangeAsync(
            "wr-feedback-rerun-failed",
            new StageDefinition(
                "check",
                [new("review", "Review", "spec/review")],
                [new("repository-check", "Repository check", "spec/repository-check")],
                RequiresApproval: true));
        await DriveToApprovalAsync(arrangement);

        var feedbackId = await arrangement.Grain.RequestChangesAsync("fix the review", "operator-1");
        var before = await RequireRunAsync(arrangement);
        var beforeEventCount = (await arrangement.Events.ListAsync(arrangement.RunId)).Count;
        var feedbackTask = (await arrangement.AssignAndClaimAsync())!;

        await arrangement.ReportTaskResultAsync(
            feedbackTask,
            output: null,
            addTasks: null,
            status: TaskReportStatus.Failed,
            detail: "feedback could not be applied");

        var after = await RequireRunAsync(arrangement);
        Assert.Equal(before.CurrentStage().Attempt, after.CurrentStage().Attempt);
        Assert.Equal(ApprovalFeedbackStatus.Open, after.Feedback.Single(f => f.Id == feedbackId).Status);
        Assert.Equal(WorkflowRunStatus.Failed, after.Status);
        Assert.DoesNotContain(
            (await arrangement.Events.ListAsync(arrangement.RunId)).Skip(beforeEventCount),
            evt => evt.Envelope.Type == EventCatalog.ReverseDns.StageStarted);
        Assert.Null(await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation"));

        var visibleFeedback = Assert.Single(await arrangement.Grain.ListFeedbackAsync());
        Assert.Equal(feedbackId, visibleFeedback.Id);
        Assert.Equal(ApprovalFeedbackStatus.Open, visibleFeedback.Status);
        Assert.Null(visibleFeedback.Resolution);
    }

    [Fact]
    public async Task ArtifactBearingFinalFeedbackTask_InitializesReplacementStageAndRetainsArtifact()
    {
        var arrangement = await ArrangeAsync(
            "wr-feedback-rerun-artifact",
            new StageDefinition(
                "check",
                [new("review", "Review", "spec/review")],
                [new("repository-check", "Repository check", "spec/repository-check")],
                RequiresApproval: true),
            finalFeedbackArtifacts: new TaskArtifactCapture([
                new TaskArtifactDeclaration("feedback.md")
            ]));
        await DriveToApprovalAsync(arrangement);

        await arrangement.Grain.RequestChangesAsync("publish the correction", "operator-1");
        var apply = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(apply);

        var publish = (await arrangement.AssignAndClaimAsync())!;
        var actionAttemptId = (await RequireRunAsync(arrangement)).CurrentStage().RunningTask!.Id;
        var uploadId = await SeedPendingUploadAsync(
            arrangement.RunId, publish.Id!, actionAttemptId, "feedback.md");
        await arrangement.ReportTaskResultAsync(
            publish,
            output: null,
            addTasks: null,
            artifactUploadIds: [uploadId]);

        var replacement = await RequireRunAsync(arrangement);
        Assert.True(replacement.CurrentStage().Initialized);
        Assert.Equal("review.s2.1", (await arrangement.AssignAndClaimAsync())!.Id);

        await using var db = CreateDb();
        var artifact = await db.WorkflowArtifacts.SingleAsync(a => a.WorkflowRunId == arrangement.RunId);
        Assert.Equal(uploadId, artifact.SourceUploadId);
        Assert.Equal(actionAttemptId, artifact.ActionAttemptId);
        Assert.Equal("feedback.md", artifact.Path);
    }

    private async Task AssertFeedbackRerunAsync(
        string runId,
        StageDefinition stage,
        IReadOnlyList<string> originalTaskIds,
        IReadOnlyList<string> checkNames,
        TaskArtifactCapture? finalFeedbackArtifacts = null)
    {
        var arrangement = await ArrangeAsync(runId, stage, finalFeedbackArtifacts);
        await DriveToApprovalAsync(arrangement);

        var initial = await RequireRunAsync(arrangement);
        var initialTasks = initial.CurrentStage().Tasks.ToDictionary(task => task.DefinitionId);
        var initialAttempt = initial.CurrentStage().Attempt;
        var initialApprovalCount = (await arrangement.Events.ListAsync(arrangement.RunId))
            .Count(evt => evt.Envelope.Type == EventCatalog.ReverseDns.StageApprovalRequested);

        var feedbackId = await arrangement.Grain.RequestChangesAsync("apply and verify the correction", "operator-1");
        var firstFeedback = (await arrangement.AssignAndClaimAsync())!;
        Assert.Equal("apply-feedback.1", firstFeedback.Id);
        await arrangement.ReportCompletedAsync(firstFeedback);

        var betweenFeedback = await RequireRunAsync(arrangement);
        Assert.Equal(initialAttempt, betweenFeedback.CurrentStage().Attempt);
        Assert.Equal(ApprovalFeedbackStatus.Open, betweenFeedback.Feedback.Single(f => f.Id == feedbackId).Status);
        var secondFeedback = (await arrangement.AssignAndClaimAsync())!;
        Assert.Equal("publish-feedback.1", secondFeedback.Id);
        Assert.Equal(initialApprovalCount, (await arrangement.Events.ListAsync(arrangement.RunId))
            .Count(evt => evt.Envelope.Type == EventCatalog.ReverseDns.StageApprovalRequested));

        var beforeFinalReport = (await arrangement.Events.ListAsync(arrangement.RunId)).Count;
        await arrangement.ReportCompletedAsync(secondFeedback);

        var finalBatch = (await arrangement.Events.ListAsync(arrangement.RunId))
            .Skip(beforeFinalReport)
            .ToList();
        Assert.DoesNotContain(
            finalBatch,
            evt => evt.Envelope.Type == EventCatalog.ReverseDns.StageApprovalRequested);
        Assert.Equal(EventCatalog.ReverseDns.StageStarted, finalBatch[^1].Envelope.Type);

        var resolved = await RequireRunAsync(arrangement);
        Assert.Equal(initialAttempt + 1, resolved.CurrentStage().Attempt);
        Assert.Equal(StageRunStatus.Running, resolved.CurrentStage().Status);
        Assert.DoesNotContain(resolved.CurrentStage().Tasks, task => task.CausedByFeedbackId is not null);
        var visibleFeedback = Assert.Single(await arrangement.Grain.ListFeedbackAsync());
        Assert.Equal(feedbackId, visibleFeedback.Id);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, visibleFeedback.Status);
        Assert.Equal("publish-feedback.1", visibleFeedback.Resolution!.ResolutionTaskId);

        foreach (var definitionId in originalTaskIds)
        {
            var rerunTask = (await arrangement.AssignAndClaimAsync())!;
            Assert.Equal($"{definitionId}.s2.1", rerunTask.Id);
            Assert.NotEqual(initialTasks[definitionId].Id, rerunTask.Id);
            await arrangement.ReportTaskResultAsync(
                rerunTask,
                JsonSerializer.SerializeToElement(new { result = $"fresh-{definitionId}" }),
                addTasks: null);
        }

        var rerunChecks = (await arrangement.AssignAndClaimAsync())!;
        Assert.Equal(WorkItemTypes.Checks, rerunChecks.WorkType);
        Assert.Equal(checkNames, rerunChecks.Items!.Select(check => check.Name));
        await arrangement.ReportCheckResultsAsync(
            rerunChecks,
            checkNames.Select(name => (name, CheckResultStatus.Passed, (string?)$"fresh-{name}"))
                .ToArray());

        var completed = await RequireRunAsync(arrangement);
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, completed.Status);
        Assert.Equal(StageRunStatus.AwaitingApproval, completed.CurrentStage().Status);
        Assert.All(
            completed.CurrentStage().Checks,
            check => Assert.Equal(StageCheckStatus.Passed, check.Status));
        Assert.Equal(
            initialApprovalCount + 1,
            (await arrangement.Events.ListAsync(arrangement.RunId))
                .Count(evt => evt.Envelope.Type == EventCatalog.ReverseDns.StageApprovalRequested));
    }

    private async Task<WorkflowGrainArrangement> ArrangeAsync(
        string runId,
        StageDefinition stage,
        TaskArtifactCapture? finalFeedbackArtifacts = null) =>
        await WorkflowGrainArrangement.CreateAsync(
            _fixture,
            runId,
            new WorkflowDefinition([stage], ApprovalDefinition(finalFeedbackArtifacts)),
            TimeProvider);

    private static async Task DriveToApprovalAsync(WorkflowGrainArrangement arrangement)
    {
        while (true)
        {
            var item = await arrangement.AssignAndClaimAsync();
            Assert.NotNull(item);
            if (item!.IsChecks)
            {
                await arrangement.ReportChecksPassAsync(item, item.Items!.Select(check => check.Name).ToArray());
                return;
            }

            await arrangement.ReportCompletedAsync(item);
        }
    }

    private static ApprovalConfig ApprovalDefinition(TaskArtifactCapture? finalFeedbackArtifacts = null) =>
        new(new ApprovalFeedbackConfig([
            new TaskDefinition("apply-feedback", "Apply approval feedback", "spec/feedback-apply"),
            new TaskDefinition("publish-feedback", "Publish approval feedback", "spec/feedback-publish", Artifacts: finalFeedbackArtifacts),
        ]));

    private async Task<string> SeedPendingUploadAsync(
        string workflowRunId,
        string workId,
        string actionAttemptId,
        string path)
    {
        await using var db = CreateDb();
        var uploadId = $"artup_{Guid.NewGuid():N}";
        db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
        {
            UploadId = uploadId,
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            ActionAttemptId = actionAttemptId,
            Path = path,
            ContentType = "text/markdown",
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Size = 42,
            StoragePath = $"workflows/{workflowRunId}/tasks/{actionAttemptId}/artifacts/{uploadId}/content",
            CreatedAt = TimeProvider.GetUtcNow(),
            ExpiresAt = TimeProvider.GetUtcNow().AddDays(1),
        });
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

    private async Task<WorkflowRun> RequireRunAsync(WorkflowGrainArrangement arrangement) =>
        await arrangement.Store.LoadAsync(arrangement.RunId)
        ?? throw new InvalidOperationException("run missing");
}
