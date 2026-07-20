using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public partial class ApprovalFeedbackTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string NextFeedbackId(WorkflowRun run) => $"fb_{run.Feedback.Count + 1}";

    private static WorkflowDefinition ApprovalStageDefinition() =>
        new("spec/workflow", [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")],
                RequiresApproval: true)
        ]);

    private static WorkflowRun BuildAwaitingApprovalRun()
    {
        var run = WorkflowRun.Create("wf-1", ApprovalStageDefinition(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.AssignTo("worker-1", TestTime.UtcNow);
        run.StartTask("draft.1", "worker-1", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        run.PassCheck(new CheckResult("plan-ok", CheckResultStatus.Passed), DateTimeOffset.UnixEpoch);
        return run;
    }

    [Fact]
    public void RequestChanges_CreatesOpenFeedback_AndResumesStageAsRunning()
    {
        var run = BuildAwaitingApprovalRun();
        var current = run.CurrentStage();
        Assert.Equal(StageRunStatus.AwaitingApproval, current.Status);
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);

        run.RequestChanges("please add a section on retry semantics", NextFeedbackId(run), DateTimeOffset.UnixEpoch);

        Assert.Single(run.Feedback);
        var feedback = run.Feedback[0];
        Assert.StartsWith("fb_", feedback.Id);
        Assert.Equal(run.Id, feedback.WorkflowRunId);
        Assert.Equal("plan", feedback.Stage);
        Assert.Equal("please add a section on retry semantics", feedback.Body);
        Assert.Equal(ApprovalFeedbackStatus.Open, feedback.Status);
        Assert.Null(feedback.ResolutionTaskId);
        Assert.Null(feedback.ResolutionSummary);
        Assert.Null(feedback.ResolvedAt);

        Assert.Equal(StageRunStatus.Running, current.Status);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Null(current.ApprovalStatus);
        Assert.Null(current.Failure);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
    }

    [Fact]
    public void RequestChanges_DoesNotMarkRunAsFailed_OrApprovalRejected()
    {
        var run = BuildAwaitingApprovalRun();

        run.RequestChanges("needs more detail", NextFeedbackId(run), DateTimeOffset.UnixEpoch);

        Assert.NotEqual(WorkflowRunStatus.Failed, run.Status);
        Assert.Null(run.Failure);
        var current = run.CurrentStage();
        Assert.NotEqual(StageRunStatus.Failed, current.Status);
        Assert.NotEqual(FailureReason.ApprovalRejected, current.Failure?.Reason);
    }

    [Fact]
    public void RequestChanges_SchedulesApplyFeedbackRuntimeTask_WithInvalidateChecks()
    {
        var run = BuildAwaitingApprovalRun();
        var current = run.CurrentStage();
        current.Checks[0].Message = "all green";

        run.RequestChanges("revise", NextFeedbackId(run), DateTimeOffset.UnixEpoch);

        var feedbackTask = current.Tasks.Last();
        Assert.Equal("apply-feedback", feedbackTask.DefinitionId);
        Assert.Equal("mohist/opencode", feedbackTask.Uses);
        Assert.NotNull(feedbackTask.CausedByFeedbackId);
        Assert.Equal(run.Feedback[0].Id, feedbackTask.CausedByFeedbackId);
        Assert.Equal(TaskRunStatus.Pending, feedbackTask.Status);

        Assert.All(current.Checks, c =>
        {
            Assert.Equal(StageCheckStatus.Pending, c.Status);
            Assert.Null(c.Message);
        });
    }

    [Fact]
    public void RequestChanges_Throws_OnNonAwaitingStage()
    {
        var run = WorkflowRun.Create("wf-2", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")])
        ]), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(
            [new("compile", "Compile", "spec/task")],
            [new("build-ok", "Build OK", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.AssignTo("worker-1", TestTime.UtcNow);
        var current = run.CurrentStage();
        Assert.Equal(StageRunStatus.Running, current.Status);

        var ex = Assert.Throws<InvalidOperationException>(() => run.RequestChanges("body", NextFeedbackId(run), DateTimeOffset.UnixEpoch));
        Assert.Contains("not awaiting approval", ex.Message);
    }

    [Fact]
    public void RequestChanges_Throws_OnEmptyOrWhitespaceBody()
    {
        var run = BuildAwaitingApprovalRun();

        Assert.Throws<ArgumentException>(() => run.RequestChanges("", NextFeedbackId(run), DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => run.RequestChanges("   ", NextFeedbackId(run), DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ApprovalFeedback_RoundTripsThroughWorkflowRunJson()
    {
        var run = BuildAwaitingApprovalRun();
        run.RequestChanges("add a quick start", NextFeedbackId(run), DateTimeOffset.UnixEpoch);

        var run2 = BuildAwaitingApprovalRun();
        run2.RequestChanges("and a troubleshooting section", NextFeedbackId(run2), DateTimeOffset.UnixEpoch);
        run.Feedback.Add(run2.Feedback[0]);

        var json = JsonSerializer.Serialize(run, JsonOptions);
        var restored = JsonSerializer.Deserialize<WorkflowRun>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Feedback.Count);
        Assert.Equal(run.Feedback[0].Id, restored.Feedback[0].Id);
        Assert.Equal(run.Feedback[0].Body, restored.Feedback[0].Body);
        Assert.Equal(run.Feedback[0].Status, restored.Feedback[0].Status);
        Assert.Equal(run.Feedback[0].CreatedAt, restored.Feedback[0].CreatedAt);
        Assert.Equal(run.Feedback[1].Stage, restored.Feedback[1].Stage);
    }

    [Fact]
    public void ApprovalFeedback_WithResolutionFields_RoundTrips()
    {
        var original = new ApprovalFeedback(
            Id: "fb_abc",
            WorkflowRunId: "wr_1",
            Stage: "plan",
            Body: "body",
            Status: ApprovalFeedbackStatus.Resolved,
            CreatedAt: new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
            ResolutionTaskId: "apply-feedback.1",
            ResolvedAt: new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
            ResolutionSummary: "Added the section");

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var restored = JsonSerializer.Deserialize<ApprovalFeedback>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void WorkflowRun_DeserializesCleanly_WhenFeedbackIsMissing()
    {
        var run = BuildAwaitingApprovalRun();
        var json = JsonSerializer.Serialize(run, JsonOptions);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            json,
            ",\"feedback\":\\[.*?\\]",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var restored = JsonSerializer.Deserialize<WorkflowRun>(stripped, JsonOptions);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.Feedback);
        Assert.Empty(restored.Feedback);
    }

    [Fact]
    public void Approve_StillWorks_AfterRequestChanges()
    {
        var run = BuildAwaitingApprovalRun();
        run.RequestChanges("first revision", NextFeedbackId(run), DateTimeOffset.UnixEpoch);

        // The apply-feedback runtime task added by RequestChanges needs to be
        // completed (Pending → Running → Completed) before the stage is ready
        // for approval again. Drive it through the full lifecycle and pass
        // the checks before asserting AwaitingApproval.
        var current = run.CurrentStage();
        var feedbackTask = current.Tasks.Last(t => t.CausedByFeedbackId is not null);
        run.StartTask(feedbackTask.Id, "worker-1", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        run.PassCheck(new CheckResult("plan-ok", CheckResultStatus.Passed), DateTimeOffset.UnixEpoch);

        Assert.Equal(StageRunStatus.AwaitingApproval, current.Status);

        run.Approve(DateTimeOffset.UnixEpoch);

        Assert.Equal(StageRunStatus.Completed, current.Status);
        Assert.Null(run.Failure);
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
    }

    [Fact]
    public void IsCurrentStageRetryableFailure_FalseWhenStageIsInActiveFeedbackLoop()
    {
        var run = BuildAwaitingApprovalRun();
        run.RequestChanges("revise", NextFeedbackId(run), DateTimeOffset.UnixEpoch);

        Assert.False(run.IsCurrentStageRetryableFailure());
    }

    [Fact]
    public void IsCurrentStageRetryableFailure_FalseWhileAwaitingApproval()
    {
        var run = BuildAwaitingApprovalRun();
        Assert.True(run.CurrentStage().IsAwaitingApproval);

        Assert.False(run.IsCurrentStageRetryableFailure());
    }

    [Fact]
    public void IsCurrentStageRetryableFailure_TrueForOrdinaryTaskFailure()
    {
        var run = WorkflowRun.Create("wf-3", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")])
        ]), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(
            [new("compile", "Compile", "spec/task")],
            [new("build-ok", "Build OK", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.AssignTo("worker-1", TestTime.UtcNow);
        run.StartTask("compile.1", "worker-1", DateTimeOffset.UnixEpoch);
        run.FailTask(new TaskResult("failed", "boom"), DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.True(run.IsCurrentStageRetryableFailure());
    }

    [Fact]
    public void IsCurrentStageRetryableFailure_FalseForRunningOrCompletedRuns()
    {
        var run = BuildAwaitingApprovalRun();
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        Assert.False(run.IsCurrentStageRetryableFailure());

        run.RequestChanges("revise", NextFeedbackId(run), DateTimeOffset.UnixEpoch);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.False(run.IsCurrentStageRetryableFailure());
    }

    [Fact]
    public void RequestChanges_WithoutFeedbackTaskOverride_UsesBuiltInApplyFeedbackDefault()
    {
        var run = BuildAwaitingApprovalRun();
        var current = run.CurrentStage();

        run.RequestChanges("apply default", NextFeedbackId(run), DateTimeOffset.UnixEpoch);

        var feedbackTask = current.Tasks.Last();
        Assert.Equal("apply-feedback", feedbackTask.DefinitionId);
        Assert.Equal("mohist/opencode", feedbackTask.Uses);
        Assert.Equal("Apply approval feedback", feedbackTask.Title);
        Assert.NotNull(feedbackTask.WithInput);
        Assert.Equal("plan", feedbackTask.WithInput!["session"]?.GetString());
        // The default feedback task binds options explicitly so approval
        // feedback honors the issue-level model selection (proposal:
        // "approval feedback task ... 显式绑定 options: ${{ vars.agent }}").
        Assert.Equal("${{ vars.agent }}", feedbackTask.WithInput!["options"]?.GetString());
    }

    [Fact]
    public void ResolveFeedbackTasks_NullConfig_ReturnsBuiltInDefault()
    {
        var task = Assert.Single(WorkflowRunExtensions.ResolveFeedbackTasks(null, "check"));

        Assert.Equal("apply-feedback", task.Id);
        Assert.Equal("Apply approval feedback", task.Title);
        Assert.Equal("mohist/opencode", task.Uses);
        Assert.NotNull(task.With);
        Assert.Equal("check", task.With!["session"]?.GetString());
        Assert.Equal("${{ vars.agent }}", task.With!["options"]?.GetString());
    }

    [Fact]
    public void RequestChanges_WithCustomFeedbackTask_UsesConfiguredTask()
    {
        var run = BuildAwaitingApprovalRun();
        var current = run.CurrentStage();

        var customWith = new Dictionary<string, System.Text.Json.JsonElement?>
        {
            ["session"] = JsonSerializer.SerializeToElement("plan"),
            ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.apply-feedback }}"),
            ["agent"] = JsonSerializer.SerializeToElement("custom-agent"),
        };
        var customTask = new TaskDefinition(
            Id: "apply-feedback",
            Title: "Apply approval feedback (custom)",
            Uses: "mohist/opencode",
            With: customWith);

        run.RequestChanges("apply with custom task", NextFeedbackId(run), DateTimeOffset.UnixEpoch, [customTask]);

        var feedbackTask = current.Tasks.Last();
        Assert.Equal("apply-feedback", feedbackTask.DefinitionId);
        Assert.Equal("Apply approval feedback (custom)", feedbackTask.Title);
        Assert.NotNull(feedbackTask.WithInput);
        Assert.True(feedbackTask.WithInput!.ContainsKey("agent"));
        Assert.Equal("custom-agent", feedbackTask.WithInput["agent"]?.GetString());
    }

    [Fact]
    public void RequestChanges_WithMultipleFeedbackTasks_ResolvesOnlyAfterAllTasksComplete()
    {
        var run = BuildAwaitingApprovalRun();
        var feedbackId = NextFeedbackId(run);
        run.RequestChanges("publish the correction", feedbackId, DateTimeOffset.UnixEpoch,
        [
            new TaskDefinition("apply-feedback", "Apply approval feedback", "mohist/opencode"),
            new TaskDefinition("publish-feedback", "Publish approval feedback", "mohist/push"),
        ]);

        var tasks = run.CurrentStage().Tasks.Where(task => task.CausedByFeedbackId == feedbackId).ToList();
        var apply = tasks.Single(task => task.DefinitionId == "apply-feedback");
        var publish = tasks.Single(task => task.DefinitionId == "publish-feedback");

        run.StartTask(apply.Id, "worker-1", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        Assert.Null(run.ResolveFeedback(feedbackId, apply.Id, JSON.DeserializeElement("\"applied\""), DateTimeOffset.UnixEpoch));

        run.StartTask(publish.Id, "worker-1", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        var resolved = run.ResolveFeedback(feedbackId, publish.Id, JSON.DeserializeElement("\"published\""), DateTimeOffset.UnixEpoch);

        Assert.NotNull(resolved);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, resolved!.Status);
        Assert.Equal(publish.Id, resolved.ResolutionTaskId);
    }

    [Fact]
    public void ResolveFeedbackTasks_ConfigWithoutSession_FillsSessionFromStage()
    {
        var config = new TaskDefinition(
            Id: "apply-feedback",
            Title: "Apply approval feedback",
            Uses: "mohist/opencode",
            With: new Dictionary<string, System.Text.Json.JsonElement?>
            {
                ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.apply-feedback }}"),
            });

        var task = Assert.Single(WorkflowRunExtensions.ResolveFeedbackTasks([config], "plan"));

        Assert.Equal("apply-feedback", task.Id);
        Assert.NotNull(task.With);
        Assert.Equal("plan", task.With!["session"]?.GetString());
        Assert.True(task.With.ContainsKey("prompt"));
    }

    [Fact]
    public void ResolveFeedbackTasks_ConfigWithSession_PreservesConfiguredSession()
    {
        var config = new TaskDefinition(
            Id: "apply-feedback",
            Title: "Apply approval feedback",
            Uses: "mohist/opencode",
            With: new Dictionary<string, System.Text.Json.JsonElement?>
            {
                ["session"] = JsonSerializer.SerializeToElement("custom-session"),
                ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.apply-feedback }}"),
            });

        var task = Assert.Single(WorkflowRunExtensions.ResolveFeedbackTasks([config], "plan"));

        Assert.Equal("custom-session", task.With!["session"]?.GetString());
    }

    [Fact]
    public void BuildFeedbackSummary_ShortBody_NotTruncated()
    {
        var summary = WorkflowRunExtensions.BuildFeedbackSummary("please add a quick start section");

        Assert.Equal("please add a quick start section", summary);
    }

    [Fact]
    public void BuildFeedbackSummary_LongBody_TruncatedWithEllipsis()
    {
        var body = new string('a', WorkflowRunExtensions.FeedbackSummaryMaxLength + 250);
        var summary = WorkflowRunExtensions.BuildFeedbackSummary(body);

        Assert.True(summary.Length <= WorkflowRunExtensions.FeedbackSummaryMaxLength + 1);
        Assert.True(summary.Length < body.Length);
        Assert.EndsWith("\u2026", summary);
    }

    [Fact]
    public void BuildFeedbackShowCommand_AllValuesPresent_FormatsCliInvocation()
    {
        var command = WorkflowRunExtensions.BuildFeedbackShowCommand(
            issueNumber: "42",
            feedbackId: "fb_abc",
            projectId: "proj_1");

        Assert.Equal(
            "mo issue feedback show 42 --feedback fb_abc --project-id proj_1 --output json",
            command);
    }

    [Fact]
    public void BuildFeedbackShowCommand_MissingMetadata_UsesLiterals()
    {
        var command = WorkflowRunExtensions.BuildFeedbackShowCommand(
            issueNumber: (string?)null,
            feedbackId: "fb_abc",
            projectId: null);

        Assert.Equal(
            "mo issue feedback show <number> --feedback fb_abc --project-id <project-id> --output json",
            command);
    }

    [Fact]
    public void ExtractResolutionSummary_EmptyOrNull_ReturnsNull()
    {
        Assert.Null(WorkflowRunExtensions.ExtractResolutionSummary(null));
        Assert.Null(WorkflowRunExtensions.ExtractResolutionSummary(""));
        Assert.Null(WorkflowRunExtensions.ExtractResolutionSummary("   \n  "));
    }

    [Fact]
    public void ExtractResolutionSummary_RawText_ReturnedAsIs()
    {
        var summary = WorkflowRunExtensions.ExtractResolutionSummary("agent finished without writing a summary");
        Assert.Equal("agent finished without writing a summary", summary);
    }

    [Fact]
    public void ExtractResolutionSummary_WithResolutionAndVerificationHeaders_StripsSections()
    {
        var output = "## Feedback Resolution\n1. src/file.cs:10 Added retry handling\n2. src/file.cs:55 Wired the new branch\n\n## Verification\n- Unit tests pass";
        var summary = WorkflowRunExtensions.ExtractResolutionSummary(output);

        Assert.Equal(
            "1. src/file.cs:10 Added retry handling\n2. src/file.cs:55 Wired the new branch",
            summary);
    }

    [Fact]
    public void ResolveFeedback_OpenFeedback_TransitionsToResolved()
    {
        var run = BuildAwaitingApprovalRun();
        run.RequestChanges("explain retry semantics", NextFeedbackId(run), DateTimeOffset.UnixEpoch);
        var feedbackId = run.Feedback[0].Id;
        var current = run.CurrentStage();
        var feedbackTask = current.Tasks.Last(t => t.DefinitionId == "apply-feedback");
        run.StartTask(feedbackTask.Id, "worker-1", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);

        var resolved = run.ResolveFeedback(feedbackId, feedbackTask.Id, JSON.DeserializeElement("\"applied retry semantics\""), DateTimeOffset.UnixEpoch);

        Assert.NotNull(resolved);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, resolved!.Status);
        Assert.Equal(feedbackTask.Id, resolved.ResolutionTaskId);
        Assert.Equal("applied retry semantics", resolved.ResolutionSummary);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, run.Feedback[0].Status);
    }

    [Fact]
    public void ResolveFeedback_UnknownFeedbackId_ReturnsNull()
    {
        var run = BuildAwaitingApprovalRun();
        run.RequestChanges("explain retry semantics", NextFeedbackId(run), DateTimeOffset.UnixEpoch);

        var resolved = run.ResolveFeedback("fb_missing", "apply-feedback.1", JSON.DeserializeElement("\"summary\""), DateTimeOffset.UnixEpoch);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveFeedback_AlreadyResolved_IsIdempotent()
    {
        var run = BuildAwaitingApprovalRun();
        run.RequestChanges("explain retry semantics", NextFeedbackId(run), DateTimeOffset.UnixEpoch);
        var feedbackId = run.Feedback[0].Id;
        var feedbackTask = run.CurrentStage().Tasks.Last(t => t.DefinitionId == "apply-feedback");
        run.StartTask(feedbackTask.Id, "worker-1", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        run.ResolveFeedback(feedbackId, feedbackTask.Id, JSON.DeserializeElement("\"first summary\""), DateTimeOffset.UnixEpoch);

        var second = run.ResolveFeedback(feedbackId, feedbackTask.Id, JSON.DeserializeElement("\"second summary\""), DateTimeOffset.UnixEpoch);

        Assert.NotNull(second);
        Assert.Equal("first summary", second!.ResolutionSummary);
    }

    [Fact]
    public void ApprovalFeedbackStatus_JsonLowercase_RoundTrips()
    {
        // Matches the spec at openspec/changes/issue-109/specs/approval-feedback-cli/spec.md:67-71
        // — the wire format must be "open" / "resolved" lowercase.
        var open = JsonSerializer.Deserialize<ApprovalFeedbackStatus>("\"open\"");
        var resolved = JsonSerializer.Deserialize<ApprovalFeedbackStatus>("\"resolved\"");
        Assert.Equal(ApprovalFeedbackStatus.Open, open);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, resolved);

        Assert.Equal("\"open\"", JsonSerializer.Serialize(ApprovalFeedbackStatus.Open));
        Assert.Equal("\"resolved\"", JsonSerializer.Serialize(ApprovalFeedbackStatus.Resolved));
    }

    [Fact]
    public void ApprovalFeedbackStatus_JsonPascalCase_IsAcceptedForBackCompat()
    {
        // Older persisted runs (and older API clients) may still carry the
        // legacy "Open" / "Resolved" PascalCase form. The converter must
        // accept those values and map them to the equivalent enum member
        // so deserializing old state does not throw.
        var open = JsonSerializer.Deserialize<ApprovalFeedbackStatus>("\"Open\"");
        var resolved = JsonSerializer.Deserialize<ApprovalFeedbackStatus>("\"Resolved\"");
        Assert.Equal(ApprovalFeedbackStatus.Open, open);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, resolved);
    }
}
