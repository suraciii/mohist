using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.Domain;

public partial class ApprovalFeedbackTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string NextFeedbackId(WorkflowRun run) => $"fb_{run.Feedback.Count + 1}";

    private const string TestOperator = "operator-1";

    private static IReadOnlyList<TaskDefinition> ConfiguredFeedbackTasks() =>
    [
        new TaskDefinition(
            "apply-feedback",
            "Apply approval feedback",
            "mohist/agent",
            new Dictionary<string, JsonElement?>
            {
                ["name"] = JsonSerializer.SerializeToElement("mohist/builder"),
                ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.apply-feedback }}"),
            })
    ];

    private static WorkflowDefinition ApprovalStageDefinition() =>
        new([
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")],
                RequiresApproval: true)
        ],
        Approval: new ApprovalConfig(new ApprovalFeedbackConfig(ConfiguredFeedbackTasks())));

    private static WorkflowRun BuildAwaitingApprovalRun()
    {
        var run = WorkflowRun.Create("wf-1", ApprovalStageDefinition(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.AssignTo("worker-1", TestTime.UtcNow);
        run.StartTask("draft.1", "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        run.PassCheck(new CheckResult("plan-ok", CheckResultStatus.Passed), DateTimeOffset.UnixEpoch);
        return run;
    }

    private static void ReinitializeAwaitingApprovalStage(WorkflowRun run, DateTimeOffset now)
    {
        run.InitializeStage(
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")],
            now);
        var task = run.CurrentStage().Tasks.Single();
        run.StartTask(task.Id, "worker-1", "test-process-generation", now);
        run.CompleteTask(now.AddSeconds(1));
        run.PassCheck(new CheckResult("plan-ok", CheckResultStatus.Passed), now.AddSeconds(2));
        Assert.Equal(StageRunStatus.AwaitingApproval, run.CurrentStage().Status);
    }

    private static void CompleteFeedbackCycle(WorkflowRun run, int cycle)
    {
        var requestAt = DateTimeOffset.UnixEpoch.AddMinutes(cycle);
        var feedbackId = $"fb_cycle_{cycle}";
        run.RequestChanges(
            $"feedback {cycle}",
            feedbackId,
            requestAt,
            TestOperator,
            ConfiguredFeedbackTasks());

        var feedbackTask = run.CurrentStage().Tasks.Last(task => task.CausedByFeedbackId == feedbackId);
        run.StartTask(feedbackTask.Id, "worker-1", "test-process-generation", requestAt);
        run.CompleteTask(requestAt.AddSeconds(1));
        var resolved = run.ResolveFeedback(
            feedbackId,
            feedbackTask.Id,
            JSON.DeserializeElement($"\"resolved feedback {cycle}\""),
            requestAt.AddSeconds(2));

        Assert.NotNull(resolved);
        run.PassCheck(new CheckResult("plan-ok", CheckResultStatus.Passed), requestAt.AddSeconds(3));
        Assert.Equal(StageRunStatus.AwaitingApproval, run.CurrentStage().Status);
    }

    [Fact]
    public void RequestChanges_CreatesOpenFeedback_AndResumesStageAsRunning()
    {
        var run = BuildAwaitingApprovalRun();
        var current = run.CurrentStage();
        Assert.Equal(StageRunStatus.AwaitingApproval, current.Status);
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);

        run.RequestChanges("please add a section on retry semantics", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());

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
        Assert.NotNull(current.ApprovalStatus);
        Assert.Equal(TestOperator, current.ApprovalStatus!.DecidedBy);
        Assert.Null(current.ApprovalStatus.Result);
        Assert.Null(current.Failure);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
    }

    [Fact]
    public void RequestChanges_DoesNotMarkRunAsFailed_OrApprovalRejected()
    {
        var run = BuildAwaitingApprovalRun();

        run.RequestChanges("needs more detail", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());

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

        run.RequestChanges("revise", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());

        var feedbackTask = current.Tasks.Last();
        Assert.Equal("apply-feedback", feedbackTask.DefinitionId);
        Assert.Equal("mohist/agent", feedbackTask.Uses);
        Assert.Equal("mohist/builder", feedbackTask.WithInput!["name"]?.GetString());
        Assert.NotNull(feedbackTask.CausedByFeedbackId);
        Assert.Equal(run.Feedback[0].Id, feedbackTask.CausedByFeedbackId);
        Assert.Equal(WorkflowActionAttemptStatus.Pending, feedbackTask.Status);

        Assert.All(current.Checks, c =>
        {
            Assert.Equal(StageCheckStatus.Pending, c.Status);
            Assert.Null(c.Message);
        });
    }

    [Fact]
    public void FeedbackRetention_AfterTwelveRequestResolveCycles_RetainsTenMostRecentEntries()
    {
        var run = BuildAwaitingApprovalRun();

        for (var cycle = 1; cycle <= 12; cycle++)
            CompleteFeedbackCycle(run, cycle);

        var expectedIds = Enumerable.Range(3, 10).Select(cycle => $"fb_cycle_{cycle}");

        Assert.Equal(10, run.Feedback.Count);
        Assert.Equal(expectedIds, run.Feedback.OrderBy(feedback => feedback.CreatedAt).Select(feedback => feedback.Id));
        Assert.All(run.Feedback, feedback => Assert.Equal(ApprovalFeedbackStatus.Resolved, feedback.Status));
    }

    [Fact]
    public void FeedbackRetention_NeverEvictsOpenFeedback_WhenResolvedEntriesAreAvailable()
    {
        var run = BuildAwaitingApprovalRun();
        for (var cycle = 1; cycle <= 10; cycle++)
        {
            run.Feedback.Add(new ApprovalFeedback(
                Id: $"resolved-{cycle}",
                WorkflowRunId: run.Id,
                Stage: "plan",
                Body: $"resolved {cycle}",
                Status: ApprovalFeedbackStatus.Resolved,
                CreatedAt: DateTimeOffset.UnixEpoch.AddMinutes(cycle)));
        }
        run.Feedback.Add(new ApprovalFeedback(
            Id: "open-existing",
            WorkflowRunId: run.Id,
            Stage: "plan",
            Body: "open existing",
            Status: ApprovalFeedbackStatus.Open,
            CreatedAt: DateTimeOffset.UnixEpoch.AddMinutes(11)));

        run.RequestChanges(
            "open new",
            "open-new",
            DateTimeOffset.UnixEpoch.AddMinutes(12),
            TestOperator,
            ConfiguredFeedbackTasks());

        Assert.Contains(run.Feedback, feedback => feedback.Id == "open-existing");
        Assert.Contains(run.Feedback, feedback => feedback.Id == "open-new");
        Assert.Equal(2, run.Feedback.Count(feedback => feedback.Status == ApprovalFeedbackStatus.Open));
        Assert.Equal(10, run.Feedback.Count);
    }

    [Fact]
    public void FeedbackRetention_RemovesOldestResolvedEntriesByCreatedAt()
    {
        var run = BuildAwaitingApprovalRun();
        for (var cycle = 11; cycle >= 1; cycle--)
        {
            run.Feedback.Add(new ApprovalFeedback(
                Id: $"resolved-{cycle}",
                WorkflowRunId: run.Id,
                Stage: "plan",
                Body: $"resolved {cycle}",
                Status: ApprovalFeedbackStatus.Resolved,
                CreatedAt: DateTimeOffset.UnixEpoch.AddMinutes(cycle)));
        }

        run.RequestChanges(
            "open new",
            "open-new",
            DateTimeOffset.UnixEpoch.AddMinutes(12),
            TestOperator,
            ConfiguredFeedbackTasks());

        Assert.DoesNotContain(run.Feedback, feedback => feedback.Id is "resolved-1" or "resolved-2");
        Assert.Equal(
            Enumerable.Range(3, 9).Select(cycle => $"resolved-{cycle}").Append("open-new"),
            run.Feedback.OrderBy(feedback => feedback.CreatedAt).Select(feedback => feedback.Id));
    }

    [Fact]
    public void ResolveFeedback_ReplacingOpenEntry_EnforcesFeedbackBound()
    {
        var run = BuildAwaitingApprovalRun();
        var feedbackId = "fb_oldest-open";
        run.RequestChanges("oldest feedback", feedbackId, DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());

        for (var cycle = 1; cycle <= 10; cycle++)
        {
            run.Feedback.Add(new ApprovalFeedback(
                Id: $"open-{cycle}",
                WorkflowRunId: run.Id,
                Stage: "plan",
                Body: $"open {cycle}",
                Status: ApprovalFeedbackStatus.Open,
                CreatedAt: DateTimeOffset.UnixEpoch.AddMinutes(cycle)));
        }

        Assert.Equal(11, run.Feedback.Count);
        Assert.All(run.Feedback, feedback => Assert.Equal(ApprovalFeedbackStatus.Open, feedback.Status));

        var task = run.CurrentStage().Tasks.Single(feedbackTask => feedbackTask.CausedByFeedbackId == feedbackId);
        run.StartTask(task.Id, "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch.AddSeconds(1), advance: false);

        var resolved = run.ResolveFeedback(
            feedbackId,
            task.Id,
            JSON.DeserializeElement("\"resolved oldest feedback\""),
            DateTimeOffset.UnixEpoch.AddSeconds(2));

        Assert.NotNull(resolved);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, resolved!.Status);
        Assert.Equal(10, run.Feedback.Count);
        Assert.DoesNotContain(run.Feedback, feedback => feedback.Id == feedbackId);
        Assert.All(run.Feedback, feedback => Assert.Equal(ApprovalFeedbackStatus.Open, feedback.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RerunAfterFailedFeedbackTask_DiscardsStaleOpenFeedback(bool fromStage)
    {
        var run = BuildAwaitingApprovalRun();
        for (var cycle = 1; cycle <= 10; cycle++)
        {
            run.Feedback.Add(new ApprovalFeedback(
                Id: $"resolved-{cycle}",
                WorkflowRunId: run.Id,
                Stage: "plan",
                Body: $"resolved {cycle}",
                Status: ApprovalFeedbackStatus.Resolved,
                CreatedAt: DateTimeOffset.UnixEpoch.AddMinutes(cycle)));
        }

        const string staleFeedbackId = "fb_stale";
        run.RequestChanges("stale feedback", staleFeedbackId, DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());
        var failedTask = run.CurrentStage().Tasks.Single(task => task.CausedByFeedbackId == staleFeedbackId);
        run.StartTask(failedTask.Id, "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
        run.FailTask(new TaskResult("failed", "feedback task failed"), DateTimeOffset.UnixEpoch.AddSeconds(1));

        if (fromStage)
            run.RerunFromStage("plan", DateTimeOffset.UnixEpoch.AddSeconds(2));
        else
            run.Rerun(DateTimeOffset.UnixEpoch.AddSeconds(2));

        Assert.DoesNotContain(run.Feedback, feedback => feedback.Id == staleFeedbackId);
        Assert.DoesNotContain(run.Feedback, feedback => feedback.Status == ApprovalFeedbackStatus.Open);
        Assert.True(run.Feedback.Count <= 10);

        ReinitializeAwaitingApprovalStage(run, DateTimeOffset.UnixEpoch.AddMinutes(20));
        const string freshFeedbackId = "fb_fresh";
        run.RequestChanges("fresh feedback", freshFeedbackId, DateTimeOffset.UnixEpoch.AddMinutes(20), TestOperator, ConfiguredFeedbackTasks());

        Assert.DoesNotContain(run.Feedback, feedback => feedback.Id == staleFeedbackId);
        Assert.Contains(run.Feedback, feedback =>
            feedback.Id == freshFeedbackId && feedback.Status == ApprovalFeedbackStatus.Open);
        Assert.True(run.Feedback.Count <= 10);
    }

    [Fact]
    public void RequestChanges_Throws_OnNonAwaitingStage()
    {
        var run = WorkflowRun.Create("wf-2", new WorkflowDefinition( [
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

        var ex = Assert.Throws<InvalidOperationException>(() => run.RequestChanges("body", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator));
        Assert.Contains("not awaiting approval", ex.Message);
    }

    [Fact]
    public void RequestChanges_Throws_OnEmptyOrWhitespaceBody()
    {
        var run = BuildAwaitingApprovalRun();

        Assert.Throws<ArgumentException>(() => run.RequestChanges("", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator));
        Assert.Throws<ArgumentException>(() => run.RequestChanges("   ", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator));
    }

    [Fact]
    public void ApprovalFeedback_RoundTripsThroughWorkflowRunJson()
    {
        var run = BuildAwaitingApprovalRun();
        run.RequestChanges("add a quick start", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());

        var run2 = BuildAwaitingApprovalRun();
        run2.RequestChanges("and a troubleshooting section", NextFeedbackId(run2), DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());
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
    public void Approve_StillWorks_AfterAnonymousRequestChanges()
    {
        var run = BuildAwaitingApprovalRun();
        run.RequestChanges("first revision", NextFeedbackId(run), DateTimeOffset.UnixEpoch, feedbackTasks: ConfiguredFeedbackTasks());

        // The configured feedback runtime task added by RequestChanges needs to be
        // completed (Pending → Running → Completed) before the stage is ready
        // for approval again. Drive it through the full lifecycle and pass
        // the checks before asserting AwaitingApproval.
        var current = run.CurrentStage();
        var feedbackTask = current.Tasks.Last(t => t.CausedByFeedbackId is not null);
        run.StartTask(feedbackTask.Id, "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
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
        run.RequestChanges("revise", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());

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
        var run = WorkflowRun.Create("wf-3", new WorkflowDefinition( [
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
        run.StartTask("compile.1", "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
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

        run.RequestChanges("revise", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.False(run.IsCurrentStageRetryableFailure());
    }

    [Fact]
    public void RequestChanges_UsesConfiguredFeedbackTaskWithoutSynthesizingSession()
    {
        var run = BuildAwaitingApprovalRun();
        var current = run.CurrentStage();

        run.RequestChanges("apply configured feedback", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());

        var feedbackTask = current.Tasks.Last();
        Assert.Equal("apply-feedback", feedbackTask.DefinitionId);
        Assert.Equal("mohist/agent", feedbackTask.Uses);
        Assert.Equal("Apply approval feedback", feedbackTask.Title);
        Assert.NotNull(feedbackTask.WithInput);
        Assert.Equal("mohist/builder", feedbackTask.WithInput!["name"]?.GetString());
        Assert.False(feedbackTask.WithInput.ContainsKey("session"));
        Assert.False(feedbackTask.WithInput.ContainsKey("options"));
    }

    public static IEnumerable<object[]> InvalidFeedbackTaskCases() =>
    [
        new object[] { null!, "at least one configured task" },
        new object[] { Array.Empty<TaskDefinition>(), "at least one configured task" },
        new object[] { new List<TaskDefinition> { null! }, "at index 0 is required" },
        new object[] { new List<TaskDefinition> { new TaskDefinition(" ", "Feedback", "spec/task") }, "requires id" },
        new object[]
        {
            new List<TaskDefinition>
            {
                new TaskDefinition("duplicate", "Feedback", "spec/task"),
                new TaskDefinition("duplicate", "Feedback again", "spec/task"),
            },
            "is duplicated",
        },
        new object[] { new List<TaskDefinition> { new TaskDefinition("feedback", "Feedback", " ") }, "requires uses" },
    ];

    [Theory]
    [MemberData(nameof(InvalidFeedbackTaskCases))]
    public void RequestChanges_InvalidFeedbackTasks_ThrowsAndLeavesRunUnchanged(
        IReadOnlyList<TaskDefinition>? feedbackTasks,
        string expectedMessage)
    {
        var run = BuildAwaitingApprovalRun();
        var current = run.CurrentStage();
        var beforeRun = JsonSerializer.Serialize(run, JsonOptions);
        var beforeApproval = JsonSerializer.Serialize(current.ApprovalStatus, JsonOptions);
        var beforeStage = JsonSerializer.Serialize(current, JsonOptions);
        var beforeTasks = JsonSerializer.Serialize(current.Tasks, JsonOptions);
        var beforeChecks = JsonSerializer.Serialize(current.Checks, JsonOptions);
        var beforeFeedback = JsonSerializer.Serialize(run.Feedback, JsonOptions);

        IReadOnlyList<WorkflowEvent>? events = null;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            events = run.RequestChanges(
                "cannot apply",
                NextFeedbackId(run),
                DateTimeOffset.UnixEpoch,
                TestOperator,
                feedbackTasks));

        Assert.Contains(expectedMessage, ex.Message);
        Assert.Null(events);
        Assert.Equal(beforeRun, JsonSerializer.Serialize(run, JsonOptions));
        Assert.Equal(beforeApproval, JsonSerializer.Serialize(current.ApprovalStatus, JsonOptions));
        Assert.Equal(beforeStage, JsonSerializer.Serialize(current, JsonOptions));
        Assert.Equal(beforeTasks, JsonSerializer.Serialize(current.Tasks, JsonOptions));
        Assert.Equal(beforeChecks, JsonSerializer.Serialize(current.Checks, JsonOptions));
        Assert.Equal(beforeFeedback, JsonSerializer.Serialize(run.Feedback, JsonOptions));
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

        run.RequestChanges("apply with custom task", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, [customTask]);

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
        run.RequestChanges("publish the correction", feedbackId, DateTimeOffset.UnixEpoch, TestOperator,
        [
            new TaskDefinition("apply-feedback", "Apply approval feedback", "mohist/opencode"),
            new TaskDefinition("publish-feedback", "Publish approval feedback", "mohist/push"),
        ]);

        var tasks = run.CurrentStage().Tasks.Where(task => task.CausedByFeedbackId == feedbackId).ToList();
        var apply = tasks.Single(task => task.DefinitionId == "apply-feedback");
        var publish = tasks.Single(task => task.DefinitionId == "publish-feedback");

        run.StartTask(apply.Id, "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        Assert.Null(run.ResolveFeedback(feedbackId, apply.Id, JSON.DeserializeElement("\"applied\""), DateTimeOffset.UnixEpoch));

        run.StartTask(publish.Id, "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        var resolved = run.ResolveFeedback(feedbackId, publish.Id, JSON.DeserializeElement("\"published\""), DateTimeOffset.UnixEpoch);

        Assert.NotNull(resolved);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, resolved!.Status);
        Assert.Equal(publish.Id, resolved.ResolutionTaskId);
    }

    [Fact]
    public void FeedbackCompletionBatch_DoesNotResolveOrRequestApprovalBeforeFinalTask()
    {
        var run = BuildAwaitingApprovalRun();
        var feedbackId = NextFeedbackId(run);
        run.RequestChanges("publish the correction", feedbackId, DateTimeOffset.UnixEpoch, TestOperator,
        [
            new TaskDefinition("apply-feedback", "Apply approval feedback", "mohist/opencode"),
            new TaskDefinition("publish-feedback", "Publish approval feedback", "mohist/push"),
        ]);

        var tasks = run.CurrentStage().Tasks.Where(task => task.CausedByFeedbackId == feedbackId).ToList();
        var apply = tasks.Single(task => task.DefinitionId == "apply-feedback");
        var publish = tasks.Single(task => task.DefinitionId == "publish-feedback");

        run.StartTask(apply.Id, "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
        var firstBatch = run.CompleteTask(DateTimeOffset.UnixEpoch.AddSeconds(1), advance: false);
        var firstResolution = run.ResolveFeedback(
            feedbackId,
            apply.Id,
            JSON.DeserializeElement("\"applied\""),
            DateTimeOffset.UnixEpoch.AddSeconds(2));

        Assert.Null(firstResolution);
        Assert.Equal(1, run.CurrentStage().Attempt);
        Assert.Equal(StageRunStatus.Running, run.CurrentStage().Status);
        Assert.Equal(ApprovalFeedbackStatus.Open, run.Feedback.Single().Status);
        Assert.DoesNotContain(firstBatch, evt => WorkflowEventSerializer.Unwrap(evt) is StageApprovalRequested);
        Assert.Equal(WorkflowActionAttemptStatus.Completed, apply.Status);
        Assert.Equal(WorkflowActionAttemptStatus.Pending, publish.Status);

        run.StartTask(publish.Id, "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch.AddSeconds(3));
        var finalBatch = run.CompleteTask(DateTimeOffset.UnixEpoch.AddSeconds(4), advance: false).ToList();
        var finalResolution = run.ResolveFeedback(
            feedbackId,
            publish.Id,
            JSON.DeserializeElement("\"published\""),
            DateTimeOffset.UnixEpoch.AddSeconds(5));

        Assert.NotNull(finalResolution);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, run.Feedback.Single().Status);
        Assert.DoesNotContain(finalBatch, evt => WorkflowEventSerializer.Unwrap(evt) is StageApprovalRequested);
    }

    [Fact]
    public void FailedFeedbackTask_LeavesFeedbackOpenWithoutStartingReplacementAttempt()
    {
        var run = BuildAwaitingApprovalRun();
        var feedbackId = NextFeedbackId(run);
        run.RequestChanges("apply the correction", feedbackId, DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());

        var feedbackTask = run.CurrentStage().Tasks.Single(task => task.CausedByFeedbackId == feedbackId);
        run.StartTask(feedbackTask.Id, "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
        var events = run.FailTask(
            new TaskResult("failed", "could not apply feedback"),
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(1, run.CurrentStage().Attempt);
        Assert.Equal(StageRunStatus.Failed, run.CurrentStage().Status);
        Assert.Equal(ApprovalFeedbackStatus.Open, run.Feedback.Single().Status);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, feedbackTask.Status);
        Assert.DoesNotContain(events, evt => WorkflowEventSerializer.Unwrap(evt) is StageStarted);
    }

    [Fact]
    public void ResolvedFeedback_RerunMaterializesFreshAttemptAndRetainsResolutionHistory()
    {
        var run = BuildAwaitingApprovalRun();
        var originalTaskId = run.CurrentStage().Tasks.Single().Id;
        var feedbackId = NextFeedbackId(run);
        run.RequestChanges("apply the correction", feedbackId, DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());

        var feedbackTask = run.CurrentStage().Tasks.Single(task => task.CausedByFeedbackId == feedbackId);
        run.StartTask(feedbackTask.Id, "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
        var completion = run.CompleteTask(DateTimeOffset.UnixEpoch.AddSeconds(1), advance: false);
        var resolved = run.ResolveFeedback(
            feedbackId,
            feedbackTask.Id,
            JSON.DeserializeElement("\"applied\""),
            DateTimeOffset.UnixEpoch.AddSeconds(2));
        Assert.NotNull(resolved);

        var rerun = run.Rerun(DateTimeOffset.UnixEpoch.AddSeconds(3));
        var batch = completion.Concat(rerun).ToList();
        run.InitializeStage(
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")],
            DateTimeOffset.UnixEpoch.AddSeconds(4),
            advance: false);

        var replacement = run.CurrentStage();
        var replacementTask = replacement.Tasks.Single();
        Assert.Equal(2, replacement.Attempt);
        Assert.NotEqual(originalTaskId, replacementTask.Id);
        Assert.Equal(WorkflowActionAttemptStatus.Pending, replacementTask.Status);
        Assert.DoesNotContain(replacement.Tasks, task => task.CausedByFeedbackId is not null);
        Assert.All(replacement.Checks, check => Assert.Equal(StageCheckStatus.Pending, check.Status));
        Assert.Equal(ApprovalFeedbackStatus.Resolved, run.Feedback.Single(feedback => feedback.Id == feedbackId).Status);
        Assert.Contains(batch, evt => WorkflowEventSerializer.Unwrap(evt) is StageStarted);
        Assert.DoesNotContain(batch, evt => WorkflowEventSerializer.Unwrap(evt) is StageApprovalRequested);
    }

    [Fact]
    public void RequestChanges_PreservesConfiguredSession()
    {
        var run = BuildAwaitingApprovalRun();
        var config = new TaskDefinition(
            Id: "apply-feedback",
            Title: "Apply approval feedback",
            Uses: "mohist/agent",
            With: new Dictionary<string, System.Text.Json.JsonElement?>
            {
                ["name"] = JsonSerializer.SerializeToElement("mohist/builder"),
                ["session"] = JsonSerializer.SerializeToElement("custom-session"),
                ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.apply-feedback }}"),
            });

        run.RequestChanges("apply with named session", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, [config]);

        var feedbackTask = run.CurrentStage().Tasks.Last();
        Assert.Equal("custom-session", feedbackTask.WithInput!["session"]?.GetString());
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
        run.RequestChanges("explain retry semantics", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());
        var feedbackId = run.Feedback[0].Id;
        var current = run.CurrentStage();
        var feedbackTask = current.Tasks.Last(t => t.DefinitionId == "apply-feedback");
        run.StartTask(feedbackTask.Id, "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
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
        run.RequestChanges("explain retry semantics", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());

        var resolved = run.ResolveFeedback("fb_missing", "apply-feedback.1", JSON.DeserializeElement("\"summary\""), DateTimeOffset.UnixEpoch);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveFeedback_AlreadyResolved_IsIdempotent()
    {
        var run = BuildAwaitingApprovalRun();
        run.RequestChanges("explain retry semantics", NextFeedbackId(run), DateTimeOffset.UnixEpoch, TestOperator, ConfiguredFeedbackTasks());
        var feedbackId = run.Feedback[0].Id;
        var feedbackTask = run.CurrentStage().Tasks.Last(t => t.DefinitionId == "apply-feedback");
        run.StartTask(feedbackTask.Id, "worker-1", "test-process-generation", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        run.ResolveFeedback(feedbackId, feedbackTask.Id, JSON.DeserializeElement("\"first summary\""), DateTimeOffset.UnixEpoch);

        var second = run.ResolveFeedback(feedbackId, feedbackTask.Id, JSON.DeserializeElement("\"second summary\""), DateTimeOffset.UnixEpoch);

        Assert.NotNull(second);
        Assert.Equal("first summary", second!.ResolutionSummary);
    }

    [Fact]
    public void ApprovalFeedbackStatus_JsonLowercase_RoundTrips()
    {
        // The wire format must be "open" / "resolved" lowercase.
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
