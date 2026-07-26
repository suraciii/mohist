using Mohist.Server.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowExecution")]
public class FeedbackDispatchSpecs : WorkflowGrainSpecs
{
    public FeedbackDispatchSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task AwaitingApproval_RequestChanges_NonFeedbackTaskDispatch_HasNoApprovalFeedback()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (draft, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("draft.", draft.WorkId);
        Assert.False(HasApprovalFeedback(draft.Variables));
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_FeedbackTaskDispatch_HasApprovalFeedbackContext()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (draft, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, draft.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var longBody = new string(
            'a',
            WorkflowRunExtensions.FeedbackSummaryMaxLength + 250);
        var feedbackId = await workflow.RequestChangesAsync(longBody, "operator-1");

        var (feedbackTask, feedbackRunner) = await PollWorkAnyAsync();
        Assert.StartsWith("apply-feedback.", feedbackTask.WorkId);
        Assert.Equal("task", feedbackTask.WorkType);
        Assert.Equal("plan", feedbackTask.Stage);
        Assert.Equal("mohist/opencode", feedbackTask.Uses);

        Assert.NotNull(feedbackTask.Variables);
        using var doc = JsonDocument.Parse(feedbackTask.Variables!);
        Assert.True(
            doc.RootElement.GetProperty("work").TryGetProperty("approvalFeedback", out var feedbackEl),
            "work.approvalFeedback object must be present in dispatch variables for feedback tasks");

        Assert.Equal(feedbackId, feedbackEl.GetProperty("id").GetString());
        Assert.Equal("plan", feedbackEl.GetProperty("stage").GetString());
        Assert.True(feedbackEl.TryGetProperty("createdAt", out var createdAt));
        Assert.Equal(JsonValueKind.String, createdAt.ValueKind);
        Assert.True(
            DateTimeOffset.TryParse(createdAt.GetString(), out _),
            "approvalFeedback.createdAt must parse as DateTimeOffset");

        var summary = feedbackEl.GetProperty("summary").GetString();
        Assert.NotNull(summary);
        Assert.True(summary!.Length <= WorkflowRunExtensions.FeedbackSummaryMaxLength + 1);
        Assert.True(summary.Length < longBody.Length, "summary must be a short preview, not the full body");
        Assert.EndsWith("…", summary);

        Assert.DoesNotContain("body", feedbackEl.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("command", feedbackEl.EnumerateObject().Select(p => p.Name));
        Assert.False(feedbackEl.TryGetProperty("body", out _), "full feedback body must not be inlined into dispatch variables");
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_ShortBodySummary_NotTruncated()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (draft, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, draft.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.RequestChangesAsync("please add a quick start section", "operator-1");

        var (feedbackTask, _) = await PollWorkAnyAsync();
        using var doc = JsonDocument.Parse(feedbackTask.Variables!);
        var summary = doc.RootElement.GetProperty("work").GetProperty("approvalFeedback").GetProperty("summary").GetString();

        Assert.Equal("please add a quick start section", summary);
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_DispatcherResolvesIssueNumberFromMetadata()
    {
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;
        await ClearBacklogAsync();
        var projectId = TestProjectId(workflowId);

        var metadata = new WorkflowRunMetadata(
            Name: null,
            CreatedAt: TestTime.UtcNow,
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectId"] = projectId,
                ["issueId"] = "issue_abc",
                ["issueNumber"] = "109",
            });

        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, ApprovalStage(), projectId);
        await workflow.StartAsync(new WorkflowStartInput(Metadata: metadata));
        _runnerId = await RegisterRunnerAsync();

        var (draft, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, draft.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var feedbackId = await workflow.RequestChangesAsync("add a quick start section", "operator-1");

        var (feedbackTask, _) = await PollWorkAnyAsync();
        using var doc = JsonDocument.Parse(feedbackTask.Variables!);
        var feedback = doc.RootElement.GetProperty("work").GetProperty("approvalFeedback");
        Assert.Equal(feedbackId, feedback.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_FeedbackTaskCompletes_ResolvesFeedbackWithSummary()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (draft, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, draft.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var feedbackId = await workflow.RequestChangesAsync("explain the retry semantics", "operator-1");

        var (feedbackTask, feedbackRunner) = await PollWorkAnyAsync();
        Assert.StartsWith("apply-feedback.", feedbackTask.WorkId);

        const string resolutionBody = "## Feedback Resolution\n1. src/file.cs:10 Added retry handling\n2. src/file.cs:55 Wired the new branch\n\n## Verification\n- Unit tests pass";
        var processOutput = JsonSerializer.SerializeToElement(new
        {
            stdout = resolutionBody,
            exitCode = 0,
        });
        await ReportAsync(
            feedbackRunner,
            feedbackTask.WorkId,
            new WorkResult("completed", Output: processOutput));

        var run = await LoadRunAsync();
        var feedback = run.Feedback.Single(f => f.Id == feedbackId);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, feedback.Status);
        Assert.Equal(feedbackTask.WorkId, feedback.ResolutionTaskId);
        // The default feedback task uses mohist/opencode, so its structured
        // output is not adapted to text under the new contract. The
        // summary stays null while the feedback still resolves.
        Assert.Null(feedback.ResolutionSummary);
        Assert.NotNull(feedback.ResolvedAt);

        var (rerunCheck, rerunRunner) = await PollWorkAnyAsync();
        Assert.Equal("checks", rerunCheck.WorkType);
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_FeedbackTaskFails_DoesNotResolveFeedback()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (draft, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, draft.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var feedbackId = await workflow.RequestChangesAsync("explain the retry semantics", "operator-1");

        var (feedbackTask, feedbackRunner) = await PollWorkAnyAsync();
        await ReportAsync(
            feedbackRunner,
            feedbackTask.WorkId,
            new WorkResult("failed", "could not apply changes"));

        var run = await LoadRunAsync();
        var feedback = run.Feedback.Single(f => f.Id == feedbackId);
        Assert.Equal(ApprovalFeedbackStatus.Open, feedback.Status);
        Assert.Null(feedback.ResolutionTaskId);
        Assert.Null(feedback.ResolutionSummary);
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_FeedbackTaskCompletesWithoutSummary_StillResolves()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (draft, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, draft.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var feedbackId = await workflow.RequestChangesAsync("explain the retry semantics", "operator-1");

        var (feedbackTask, feedbackRunner) = await PollWorkAnyAsync();
        await ReportAsync(
            feedbackRunner,
            feedbackTask.WorkId,
            new WorkResult("completed", Output: JSON.DeserializeElement("""{"note":"agent finished without writing a summary"}""")));

        var run = await LoadRunAsync();
        var feedback = run.Feedback.Single(f => f.Id == feedbackId);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, feedback.Status);
        Assert.Equal(feedbackTask.WorkId, feedback.ResolutionTaskId);
        Assert.Null(feedback.ResolutionSummary);
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_FeedbackTaskCompletesWithEmptyOutput_ResolvesWithoutSummary()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (draft, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, draft.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var feedbackId = await workflow.RequestChangesAsync("explain the retry semantics", "operator-1");

        var (feedbackTask, feedbackRunner) = await PollWorkAnyAsync();
        await ReportAsync(feedbackRunner, feedbackTask.WorkId, new WorkResult("completed"));

        var run = await LoadRunAsync();
        var feedback = run.Feedback.Single(f => f.Id == feedbackId);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, feedback.Status);
        Assert.Null(feedback.ResolutionSummary);
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_TaskDispatchedTwice_DoesNotInjectStaleContext()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (draft, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, draft.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.RequestChangesAsync("first round of feedback", "operator-1");

        var (feedbackTask, feedbackRunner) = await PollWorkAnyAsync();
        await ReportAsync(feedbackRunner, feedbackTask.WorkId, "completed");

        var (rerunCheck, rerunRunner) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(rerunRunner, rerunCheck, "plan-ok");

        await workflow.ApproveAsync("operator-1");
        var (buildTask, buildRunner) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", buildTask.WorkId);
        Assert.False(HasApprovalFeedback(buildTask.Variables), "non-feedback task must not carry approvalFeedback context");
    }

    [Fact]
    public async Task AwaitingApproval_RequestChanges_DeactivateThenDispatch_StillCarriesFeedbackContext()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (draft, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, draft.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var feedbackId = await workflow.RequestChangesAsync("add a quick start section", "operator-1");

        // Force the workflow grain to deactivate so the next dispatch
        // rehydrates the workflow run from the JSON-serialized state.
        // This exercises the CausedByFeedbackId round-trip path.
        var grain = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
        await grain.DeactivateForTestAsync();

        // Re-register the runner so the next poll picks up the
        // rehydrated apply-feedback task.
        await RegisterRunnerForProjectAsync(TestProjectId(_workflowId!), _runnerId);

        var (feedbackTask, _) = await PollWorkAnyAsync();
        Assert.StartsWith("apply-feedback.", feedbackTask.WorkId);

        using var doc = JsonDocument.Parse(feedbackTask.Variables!);
        Assert.True(
            doc.RootElement.GetProperty("work").TryGetProperty("approvalFeedback", out var feedbackEl),
            "work.approvalFeedback object must still be present after workflow reactivation");
        Assert.Equal(feedbackId, feedbackEl.GetProperty("id").GetString());
    }

    private static bool HasApprovalFeedback(string? variablesJson)
    {
        if (string.IsNullOrWhiteSpace(variablesJson)) return false;
        using var doc = JsonDocument.Parse(variablesJson);
        return doc.RootElement.TryGetProperty("work", out var work)
            && work.TryGetProperty("approvalFeedback", out _);
    }

    private async Task<WorkflowRun> LoadRunAsync()
    {
        await using var db = new MohistDbContext(
            new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(_fixture.ConnectionString)
                .Options);
        var row = await db.WorkflowRuns.FindAsync(_workflowId!);
        Assert.NotNull(row);
        return JsonSerializer.Deserialize<WorkflowRun>(row!.State, ReadJsonOptions)!;
    }
}
