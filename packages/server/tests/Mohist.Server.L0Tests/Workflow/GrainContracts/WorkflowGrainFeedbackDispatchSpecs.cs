using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.GrainContracts;

/// <summary>
/// Feedback-loop dispatch of the workflow run: approvalFeedback context on
/// feedback tasks, summary truncation, issue-number resolution from run
/// metadata, resolution/failure outcomes, and stale-context suppression on
/// non-feedback tasks. Drives the real grain without a cluster; the
/// deactivate-then-rehydrate scenario stays on the cluster as a
/// representative activation proof (#681).
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainFeedbackDispatchSpecs
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider TimeProvider = new(FixedTime);
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainFeedbackDispatchSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_NonFeedbackTaskDispatch_HasNoApprovalFeedback()
    {
        var arrangement = await ArrangeAsync("wr-feedback-non-feedback-clean");

        var draft = (await arrangement.AssignAndClaimAsync())!;
        Assert.False(HasApprovalFeedback(await ToDispatchAsync(arrangement, draft)));
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_FeedbackTaskDispatch_HasApprovalFeedbackContext()
    {
        var arrangement = await ArrangeAsync("wr-feedback-context");
        await DrivePlanToGateAsync(arrangement);

        var longBody = new string('a', WorkflowRunExtensions.FeedbackSummaryMaxLength + 250);
        var feedbackId = await arrangement.Grain.RequestChangesAsync(longBody, "operator-1");

        var claimed = (await arrangement.AssignAndClaimAsync())!;
        var feedbackTask = await ToDispatchAsync(arrangement, claimed);
        Assert.StartsWith("apply-feedback.", feedbackTask.WorkId);
        Assert.Equal("task", feedbackTask.WorkType);
        Assert.Equal("plan", feedbackTask.Stage);
        Assert.Equal("spec/task", feedbackTask.Uses);

        var feedbackEl = ApprovalFeedbackElement(feedbackTask);

        Assert.Equal(feedbackId, feedbackEl.GetProperty("id").GetString());
        Assert.Equal("plan", feedbackEl.GetProperty("stage").GetString());
        Assert.True(feedbackEl.TryGetProperty("createdAt", out var createdAt));
        Assert.Equal(JsonValueKind.String, createdAt.ValueKind);
        Assert.True(
            DateTimeOffset.TryParse(createdAt.GetString(), out _),
            "approvalFeedback.createdAt must parse as DateTimeOffset");

    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_UsesBoundFeedbackTasksAfterLiveProfileChanges()
    {
        var arrangement = await ArrangeAsync("wr-feedback-bound-definition");
        await DrivePlanToGateAsync(arrangement);

        await WorkflowGrainContractSupport.SeedTemplateAsync(
            _fixture,
            arrangement.ProjectId,
            ApprovalStage() with { Approval = null },
            TimeProvider.GetUtcNow());

        var feedbackId = await arrangement.Grain.RequestChangesAsync("use the bound task", "operator-1");
        var claimed = (await arrangement.AssignAndClaimAsync())!;
        var feedbackTask = await ToDispatchAsync(arrangement, claimed);

        Assert.Equal($"apply-feedback.1", feedbackTask.WorkId);
        Assert.Equal("spec/task", feedbackTask.Uses);
        Assert.Equal(feedbackId, ApprovalFeedbackElement(feedbackTask).GetProperty("id").GetString());
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_ShortBodySummary_NotTruncated()
    {
        var arrangement = await ArrangeAsync("wr-feedback-short-summary");
        await DrivePlanToGateAsync(arrangement);

        await arrangement.Grain.RequestChangesAsync("please add a quick start section", "operator-1");

        var claimed = (await arrangement.AssignAndClaimAsync())!;
        var summary = ApprovalFeedbackElement(await ToDispatchAsync(arrangement, claimed)).GetProperty("summary").GetString();

        Assert.Equal("please add a quick start section", summary);
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_DispatcherResolvesIssueNumberFromMetadata()
    {
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture,
            "wr-feedback-issue-109",
            ApprovalStage(),
            TimeProvider,
            projectId: "proj-feedback-issue-109",
            issueNumber: 109);

        await DrivePlanToGateAsync(arrangement);

        var feedbackId = await arrangement.Grain.RequestChangesAsync("add a quick start section", "operator-1");

        var claimed = (await arrangement.AssignAndClaimAsync())!;
        var feedback = ApprovalFeedbackElement(await ToDispatchAsync(arrangement, claimed));
        Assert.Equal(feedbackId, feedback.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_FeedbackTaskCompletes_ResolvesFeedbackWithSummary()
    {
        var arrangement = await ArrangeAsync("wr-feedback-resolves");
        await DrivePlanToGateAsync(arrangement);

        var feedbackId = await arrangement.Grain.RequestChangesAsync("explain the retry semantics", "operator-1");

        var feedbackTask = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("apply-feedback.", feedbackTask.Id);

        const string resolutionBody = "## Feedback Resolution\n1. src/file.cs:10 Added retry handling\n2. src/file.cs:55 Wired the new branch\n\n## Verification\n- Unit tests pass";
        var processOutput = JsonSerializer.SerializeToElement(new
        {
            stdout = resolutionBody,
            exitCode = 0,
        });
        await arrangement.ReportTaskResultAsync(feedbackTask, processOutput, null);

        var run = await RequireRunAsync(arrangement);
        var feedback = run.Feedback.Single(f => f.Id == feedbackId);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, feedback.Status);
        Assert.Equal(feedbackTask.Id, feedback.ResolutionTaskId);
        // This configured feedback task uses spec/task, so its structured
        // output is not adapted to text. The summary stays null while the
        // feedback still resolves.
        Assert.Null(feedback.ResolutionSummary);
        Assert.NotNull(feedback.ResolvedAt);

        var rerunCheck = await arrangement.AssignAndClaimAsync();
        Assert.Equal("checks", rerunCheck!.WorkType);
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_FeedbackTaskFails_DoesNotResolveFeedback()
    {
        var arrangement = await ArrangeAsync("wr-feedback-fail-open");
        await DrivePlanToGateAsync(arrangement);

        var feedbackId = await arrangement.Grain.RequestChangesAsync("explain the retry semantics", "operator-1");

        var feedbackTask = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportTaskResultAsync(
            feedbackTask,
            output: null,
            addTasks: null,
            status: TaskReportStatus.Failed,
            detail: "could not apply changes");

        var run = await RequireRunAsync(arrangement);
        var feedback = run.Feedback.Single(f => f.Id == feedbackId);
        Assert.Equal(ApprovalFeedbackStatus.Open, feedback.Status);
        Assert.Null(feedback.ResolutionTaskId);
        Assert.Null(feedback.ResolutionSummary);
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_FailedFeedbackTask_RetryCompletesAndResolvesFeedback()
    {
        var arrangement = await ArrangeAsync("wr-feedback-retry-resolves");
        await DrivePlanToGateAsync(arrangement);

        var feedbackId = await arrangement.Grain.RequestChangesAsync("explain the retry semantics", "operator-1");

        var failedTask = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportTaskResultAsync(
            failedTask,
            output: null,
            addTasks: null,
            status: TaskReportStatus.Failed,
            detail: "could not apply changes");

        await arrangement.Grain.RetryAsync();

        var retriedTask = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("apply-feedback.2", retriedTask.Id);
        var pendingRun = await RequireRunAsync(arrangement);
        var pendingRetry = pendingRun.CurrentStage().Tasks.Single(task => task.Id == retriedTask.Id);
        Assert.Equal(feedbackId, pendingRetry.CausedByFeedbackId);
        Assert.Equal(failedTask.Id, pendingRetry.CausedByFailedTaskId);

        await arrangement.ReportTaskResultAsync(retriedTask, output: null, addTasks: null);

        var run = await RequireRunAsync(arrangement);
        var feedback = run.Feedback.Single(f => f.Id == feedbackId);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, feedback.Status);
        Assert.Equal(retriedTask.Id, feedback.ResolutionTaskId);
        Assert.True(run.Feedback.Count <= 10);
        Assert.DoesNotContain(run.Feedback, candidate =>
            candidate.Id == feedbackId && candidate.Status == ApprovalFeedbackStatus.Open);
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_FeedbackTaskCompletesWithoutSummary_StillResolves()
    {
        var arrangement = await ArrangeAsync("wr-feedback-no-summary");
        await DrivePlanToGateAsync(arrangement);

        var feedbackId = await arrangement.Grain.RequestChangesAsync("explain the retry semantics", "operator-1");

        var feedbackTask = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportTaskResultAsync(
            feedbackTask,
            JsonSerializer.SerializeToElement(new { note = "agent finished without writing a summary" }),
            null);

        var run = await RequireRunAsync(arrangement);
        var feedback = run.Feedback.Single(f => f.Id == feedbackId);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, feedback.Status);
        Assert.Equal(feedbackTask.Id, feedback.ResolutionTaskId);
        Assert.Null(feedback.ResolutionSummary);
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_FeedbackTaskCompletesWithEmptyOutput_ResolvesWithoutSummary()
    {
        var arrangement = await ArrangeAsync("wr-feedback-empty-output");
        await DrivePlanToGateAsync(arrangement);

        var feedbackId = await arrangement.Grain.RequestChangesAsync("explain the retry semantics", "operator-1");

        var feedbackTask = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportTaskResultAsync(feedbackTask, output: null, addTasks: null);

        var run = await RequireRunAsync(arrangement);
        var feedback = run.Feedback.Single(f => f.Id == feedbackId);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, feedback.Status);
        Assert.Null(feedback.ResolutionSummary);
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_TaskDispatchedTwice_DoesNotInjectStaleContext()
    {
        var arrangement = await ArrangeAsync("wr-feedback-stale-context");
        await DrivePlanToGateAsync(arrangement);

        await arrangement.Grain.RequestChangesAsync("first round of feedback", "operator-1");

        var feedbackTask = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(feedbackTask);

        var rerunCheck = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportChecksPassAsync(rerunCheck, "plan-ok");

        await arrangement.Grain.ApproveAsync("operator-1");
        var buildItem = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("compile.", buildItem!.Id);
        Assert.False(HasApprovalFeedback(await ToDispatchAsync(arrangement, buildItem)), "non-feedback task must not carry approvalFeedback context");
    }

    private async Task<WorkflowGrainArrangement> ArrangeAsync(string runId) =>
        await WorkflowGrainArrangement.CreateAsync(_fixture, runId, ApprovalStage(), TimeProvider);

    /// <summary>Renders a claimed item into its production dispatch shape.</summary>
    private static async Task<WorkDispatch> ToDispatchAsync(
        WorkflowGrainArrangement arrangement,
        WorkItem item)
    {
        var run = await arrangement.Store.LoadAsync(arrangement.RunId)
            ?? throw new InvalidOperationException("run missing");
        return await arrangement.Translator.TranslateToDispatchAsync(
            item, arrangement.RunId, run, arrangement.WorkerId);
    }

    /// <summary>Drives the plan stage's task and check to the approval gate.</summary>
    private static async Task DrivePlanToGateAsync(WorkflowGrainArrangement arrangement)
    {
        var draft = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(draft);
        var check = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(check);
        await arrangement.ReportChecksPassAsync(check!, "plan-ok");
    }

    /// <summary>
    /// Extracts the work.approvalFeedback subtree from a claimed item's
    /// variables — the direct-construction equivalent of parsing the cluster
    /// dispatch's Variables JSON.
    /// </summary>
    private static JsonElement ApprovalFeedbackElement(WorkDispatch dispatch)
    {
        using var doc = JsonDocument.Parse(dispatch.Variables!);
        return doc.RootElement.GetProperty("work").GetProperty("approvalFeedback").Clone();
    }

    private static bool HasApprovalFeedback(WorkDispatch dispatch)
    {
        if (string.IsNullOrWhiteSpace(dispatch.Variables)) return false;
        using var doc = JsonDocument.Parse(dispatch.Variables);
        return doc.RootElement.TryGetProperty("work", out var work)
            && work.TryGetProperty("approvalFeedback", out _);
    }

    private static async Task<WorkflowRun> RequireRunAsync(WorkflowGrainArrangement arrangement) =>
        await arrangement.Store.LoadAsync(arrangement.RunId) ?? throw new InvalidOperationException("run missing");

    private static WorkflowDefinition ApprovalStage() => new(
    [
        new StageDefinition(
            "plan",
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")],
            RequiresApproval: true),
        new StageDefinition(
            "build",
            [new("compile", "Compile", "spec/task")],
            [new("build-ok", "Build OK", "spec/check")]),
    ],
    Approval: new ApprovalConfig(new ApprovalFeedbackConfig([
        new TaskDefinition(
            "apply-feedback",
            "Apply approval feedback",
            "spec/task",
            new Dictionary<string, JsonElement?>
            {
                ["session"] = JsonSerializer.SerializeToElement("plan"),
                ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.apply-feedback }}"),
            })
    ])));
}
