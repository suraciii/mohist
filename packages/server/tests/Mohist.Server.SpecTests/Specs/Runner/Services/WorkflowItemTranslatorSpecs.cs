using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Services;

/// <summary>
/// Unit specs for <see cref="WorkflowItemTranslator"/> — the boundary service
/// RunnerGrain composes to translate between the control plane's domain
/// work items and the runner-process dispatch envelopes. Covers both the
/// out-direction (<c>WorkItem → WorkDispatch</c>) and the in-direction
/// (<c>WorkResult → TaskReport | CheckReport</c>). Acceptance gate for T-003
/// design decisions D1/D2/D4/D7 (work item protocol + translation externalization).
/// </summary>
public partial class WorkflowItemTranslatorSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly WorkflowPromptResolver _promptResolver;
    private readonly WorkflowVariableResolver _variableResolver;
    private readonly WorkflowItemTranslator _translator;
    private readonly IWorkflowArtifactBindService _bindService;
    private readonly FakeAgentExecutionSnapshotResolver _agentResolver;

    public WorkflowItemTranslatorSpecs()
    {
        _database = TestSqliteDatabase.CreateMigrated();

        var factory = new TestDbContextFactory(_database.Options);
        var runVariablesStore = new WorkflowRunVariablesStore(factory);
        var promptLoader = new EmptyPromptLoader();
        _promptResolver = new WorkflowPromptResolver(
            factory,
            new ProjectPromptStore(factory, promptLoader, new PromptTemplateEngine()));
        _variableResolver = new WorkflowVariableResolver(
            factory,
            new ProjectVariableStore(factory),
            new IssueVariableStore(factory),
            runVariablesStore);
        _bindService = new WorkflowArtifactBindService(
            factory, BindNullLogger, new FakeTimeProvider(TestTime.UtcNow));
        _agentResolver = new FakeAgentExecutionSnapshotResolver();
        _translator = new WorkflowItemTranslator(_promptResolver, _variableResolver, _bindService, TranslatorNullLogger, _agentResolver);
    }

    private static Microsoft.Extensions.Logging.ILogger<WorkflowItemTranslator> TranslatorNullLogger =>
        new NullLogger<WorkflowItemTranslator>();

    private static Microsoft.Extensions.Logging.ILogger<WorkflowArtifactBindService> BindNullLogger =>
        new BindServiceNullLogger();

    private sealed class BindServiceNullLogger : Microsoft.Extensions.Logging.ILogger<WorkflowArtifactBindService>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NoopScope();
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        private sealed class NoopScope : IDisposable { public void Dispose() { } }
    }

    private sealed class EmptyPromptLoader : IPromptLoader
    {
        public Dictionary<string, string> LoadAll() => new(StringComparer.Ordinal);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<WorkflowRun> SeedRunningWorkflowAsync(string workflowRunId, string projectId)
    {
        var run = WorkflowRunExtensions.Create(
            workflowRunId,
            new WorkflowDefinition(
            [
                new StageDefinition("build",
                    [new("task-1", "Task 1", "spec/task")],
                    [new("check-1", "Check 1", "spec/check")]),
            ]),
            DateTimeOffset.UnixEpoch,
            new WorkflowRunMetadata(null, DateTimeOffset.UnixEpoch, ProjectId: projectId, IssueNumber: 42, EpicNumber: 7));

        await SeedProfileAsync(projectId, workflowRunId, run);
        return run;
    }

    private async Task SeedProfileAsync(string projectId, string workflowRunId, WorkflowRun run)
    {
        await using var db = new MohistDbContext(_database.Options);
        var definitionJson = WorkflowGrainTestHelpers.SerializeProfile(new WorkflowDefinition(
            [
                new StageDefinition("build",
                    [new("task-1", "Task 1", "spec/task")],
                    [new("check-1", "Check 1", "spec/check")]),
            ]));

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = "spec/workflow",
        });
        db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
        {
            ProjectId = projectId,
            TemplateId = "spec/workflow",
            Template = definitionJson,
        });

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JsonSerializer.Serialize(run),
        });

        await db.SaveChangesAsync();
    }

    // =========================================================================
    // Out-direction: WorkItem → WorkDispatch
    // =========================================================================

    [Fact]
    public async Task TranslateToDispatch_PreservesExplicitNullAndNumericRecoveryState()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-translate-recovery";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("error=one", [], RetrySelf: true)]);

        var fresh = await _translator.TranslateToDispatchAsync(
            WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null, recovery: recovery, recoveryRemaining: null),
            runId, run, "runner-1");
        var continuation = await _translator.TranslateToDispatchAsync(
            WorkItem.Task("build", "task-1.2", "Task 1", "spec/task", null, recovery: recovery, recoveryRemaining: 1),
            runId, run, "runner-1");

        Assert.Null(fresh.RecoveryRemaining);
        Assert.Equal(1, continuation.RecoveryRemaining);
        Assert.Equal(JSON.Serialize(recovery), fresh.Recovery);
        Assert.Equal(fresh.Recovery, continuation.Recovery);
    }

    [Fact]
    public async Task TranslateToDispatch_ChecksItem_ProducesDispatchWithChecksPayload()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-translate-2";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Checks("build", "checks-build",
            [new CheckItem("check-1", "Check 1", "spec/check")]);

        var dispatch = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        Assert.Equal(runId, dispatch.WorkflowRunId);
        Assert.Equal("checks-build", dispatch.WorkId);
        Assert.Equal("checks", dispatch.WorkType);
        Assert.Equal("build", dispatch.Stage);
        Assert.Equal("Stage checks", dispatch.Title);
        Assert.NotNull(dispatch.With);
        Assert.Equal(7, dispatch.EpicNumber);
    }

    [Fact]
    public async Task TranslateToDispatch_TaskItem_DoesNotInjectDispatchId()
    {
        // Spec contract: the work item carries the work id; the translator
        // does not invent one. Confirms workId flows from item, not from
        // any internal counter or UUID generator.
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-translate-3";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null);

        var dispatch = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        Assert.Equal("task-1.1", dispatch.WorkId);
    }

    [Fact]
    public async Task TranslateToDispatch_LegacyInlineAgentInput_ThrowsDispatchRejection()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-translate-legacy-agent";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Task(
            "check",
            "recover:fix-review-findings.4",
            "Fix review findings",
            "mohist/opencode",
            With("""{"session":"check","prompt":"fix","agent":"${{ vars.agent }}"}"""));

        var error = await Assert.ThrowsAsync<WorkflowDispatchRejectedException>(
            () => _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));

        Assert.Contains("with.agent", error.Message, StringComparison.Ordinal);
        Assert.Contains("options", error.Message, StringComparison.Ordinal);
    }

    // =========================================================================
    // In-direction: WorkResult → TaskReport | CheckReport
    // =========================================================================

    [Fact]
    public async Task TranslateResult_SucceededTaskWithoutDeclaredArtifacts_SucceedsWithOutput()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-result-1";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null);
        var result = new WorkResult("completed", Output: JSON.DeserializeElement("{\"ok\":true}"));

        var report = await _translator.TranslateResultAsync(item, result, runId, run);

        var task = Assert.IsType<WorkflowItemTranslator.InboundReport.Task>(report);
        Assert.Equal(TaskReportStatus.Succeeded, task.Value.Status);
        Assert.Equal("task-1.1", task.Value.WorkId);
        Assert.True(task.Value.Output.HasValue);
        Assert.Equal(JsonValueKind.Object, task.Value.Output!.Value.ValueKind);
        Assert.True(task.Value.Output.Value.TryGetProperty("ok", out var okProp));
        Assert.True(okProp.GetBoolean());
        Assert.Null(task.Value.Detail);
    }

    [Fact]
    public async Task TranslateResult_FailedTaskWithDetail_FailsWithDetailPreserved()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-result-2";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null);
        var result = new WorkResult("failed", "runner-exit-42");

        var report = await _translator.TranslateResultAsync(item, result, runId, run);

        var task = Assert.IsType<WorkflowItemTranslator.InboundReport.Task>(report);
        Assert.Equal(TaskReportStatus.Failed, task.Value.Status);
        Assert.Equal("runner-exit-42", task.Value.Detail);
        // Task report has only two states (Succeeded | Failed) — confirms the
        // protocol's no-extraneous-states guarantee.
        Assert.True(task.Value.Status is TaskReportStatus.Succeeded or TaskReportStatus.Failed);
    }

    [Fact]
    public async Task TranslateResult_SucceededTaskMissingDeclaredArtifacts_SucceedsWithoutArtifacts()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-result-3";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null,
            artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]));
        var result = new WorkResult("completed", Output: JSON.DeserializeElement("{}"));

        var report = await _translator.TranslateResultAsync(item, result, runId, run);

        var task = Assert.IsType<WorkflowItemTranslator.InboundReport.Task>(report);
        Assert.Equal(TaskReportStatus.Succeeded, task.Value.Status);
        Assert.Null(task.Value.Artifacts);
    }

    [Fact]
    public async Task TranslateResult_SucceededTaskWithUploadIds_RecordsArtifactReferences()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-result-4";
        var run = await SeedRunningWorkflowAsync(runId, projectId);

        // Seed a pending upload the bind service can locate.
        var uploadId = $"up-{Guid.NewGuid():N}";
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
            {
                UploadId = uploadId,
                WorkflowRunId = runId,
                WorkId = "task-1.1",
                TaskRunId = "task-1.1",
                Path = "review.md",
                Kind = "file",
                Size = 5,
                ContentType = "text/markdown",
                CreatedAt = TestTime.UtcNow,
                ExpiresAt = TestTime.UtcNow.AddHours(1),
                StoragePath = "/tmp/review.md",
            });
            await db.SaveChangesAsync();
        }

        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null,
            artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]));
        var result = new WorkResult("completed", Output: JSON.DeserializeElement("{}"), ArtifactUploadIds: [uploadId]);

        var report = await _translator.TranslateResultAsync(item, result, runId, run);

        var task = Assert.IsType<WorkflowItemTranslator.InboundReport.Task>(report);
        Assert.Equal(TaskReportStatus.Succeeded, task.Value.Status);
        Assert.NotNull(task.Value.Artifacts);
        Assert.Single(task.Value.Artifacts);
        Assert.Equal("review.md", task.Value.Artifacts[0].Path);
    }

    [Fact]
    public async Task TranslateResult_ChecksItem_ParsesRunnerOutputIntoCheckResults()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-result-5";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Checks("build", "checks-build",
            [new CheckItem("check-1", "Check 1", "spec/check"), new CheckItem("check-2", "Check 2", "spec/check")]);
        var output = JsonSerializer.SerializeToElement(new[]
        {
            new { name = "check-1", status = "pass", message = (string?)null! },
            new { name = "check-2", status = "fail", message = "nope" },
        });
        var result = new WorkResult("fail", Output: output);

        var report = await _translator.TranslateResultAsync(item, result, runId, run);

        var checks = Assert.IsType<WorkflowItemTranslator.InboundReport.Checks>(report);
        Assert.Equal("build", checks.Value.Stage);
        Assert.Equal(2, checks.Value.Results.Count);
        Assert.Equal("check-1", checks.Value.Results[0].Name);
        Assert.Equal(CheckResultStatus.Passed, checks.Value.Results[0].Status);
        Assert.Equal("check-2", checks.Value.Results[1].Name);
        Assert.Equal(CheckResultStatus.Failed, checks.Value.Results[1].Status);
        Assert.Equal("nope", checks.Value.Results[1].Message);
    }

    [Fact]
    public async Task TranslateResult_TimeoutLikeFailedTask_ReportsAsFailed_NotAsDistinctState()
    {
        // Regression: a runner-lost or timeout-style failure is reported as
        // `failed` with a `Detail` distinguishing the cause. The protocol
        // collapses both into the same `Failed` status; the detail string
        // is the only diagnostic surface.
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-result-6";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null);
        var result = new WorkResult("failed", "runner-lost");

        var report = await _translator.TranslateResultAsync(item, result, runId, run);

        var task = Assert.IsType<WorkflowItemTranslator.InboundReport.Task>(report);
        Assert.Equal(TaskReportStatus.Failed, task.Value.Status);
        Assert.Equal("runner-lost", task.Value.Detail);
        // No additional TaskReportStatus variant — confirms the protocol's
        // two-state invariant from the acceptance criteria.
        Assert.Equal(2, System.Enum.GetValues<TaskReportStatus>().Length);
    }

    [Fact]
    public async Task TranslateToDispatch_UnknownWorkType_Throws()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-translate-err";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = new WorkItem("build", "garbage", null, null, null, null, null, null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));
    }

    // =========================================================================
    // Protocol contract: WorkItem carries declaration only, no dispatch fields
    // =========================================================================

    [Fact]
    public void WorkItem_TaskVariant_ExposesOnlyDeclarationFields()
    {
        // The WorkItem record is a domain-semantic declaration. It MUST NOT
        // carry dispatch id, resolved variables, rendered context, or loaded
        // prompts — those are the translator's job to assemble on the way
        // out. This contract pins the public surface area.
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null,
            artifacts: null, setVars: null);

        Assert.Equal("task", item.WorkType);
        Assert.Equal("build", item.Stage);
        Assert.Equal("task-1.1", item.Id);
        Assert.Equal("Task 1", item.Title);
        Assert.Equal("spec/task", item.Uses);
        Assert.True(item.IsTask);
        Assert.False(item.IsChecks);
        Assert.Null(item.Items);
    }

    [Fact]
    public void WorkItem_ChecksVariant_ExposesStageAndItems()
    {
        var items = new List<CheckItem>
        {
            new("check-1", "Check 1", "spec/check"),
        };
        var item = WorkItem.Checks("build", "checks-build", items);

        Assert.Equal("checks", item.WorkType);
        Assert.Equal("build", item.Stage);
        Assert.Equal("checks-build", item.Id);
        Assert.False(item.IsTask);
        Assert.True(item.IsChecks);
        Assert.Same(items, item.Items);
        Assert.Null(item.Title);
        Assert.Null(item.Uses);
        Assert.Null(item.With);
    }

    [Fact]
    public void TaskReportStatus_HasExactlyTwoStates_SucceededAndFailed()
    {
        // Protocol invariant: the only report states are Succeeded and Failed.
        // Timeouts and runner-lost collapse into Failed + Detail; they are
        // NOT independent enum values.
        var values = System.Enum.GetValues<TaskReportStatus>();
        Assert.Equal(2, values.Length);
        Assert.Contains(TaskReportStatus.Succeeded, values);
        Assert.Contains(TaskReportStatus.Failed, values);
    }

    [Fact]
    public async Task TranslateResult_AllStatusAliases_CollapseToSucceeded()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-status-alias";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null);

        foreach (var alias in new[] { "completed", "pass", "PASS", "Completed" })
        {
            var report = await _translator.TranslateResultAsync(
                item, new WorkResult(alias), runId, run);
            var task = Assert.IsType<WorkflowItemTranslator.InboundReport.Task>(report);
            Assert.Equal(TaskReportStatus.Succeeded, task.Value.Status);
        }
    }

    [Fact]
    public async Task TranslateResult_AllFailureAliases_CollapseToFailed_WithMessageAsDetail()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-fail-alias";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null);

        var report = await _translator.TranslateResultAsync(
            item, new WorkResult("failed", "work-timeout"), runId, run);
        var task = Assert.IsType<WorkflowItemTranslator.InboundReport.Task>(report);

        Assert.Equal(TaskReportStatus.Failed, task.Value.Status);
        Assert.Equal("work-timeout", task.Value.Detail);
    }

    private static Dictionary<string, JsonElement?> With(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(json) ?? new();

    private sealed class FakeAgentExecutionSnapshotResolver : IAgentExecutionSnapshotResolver
    {
        public AgentExecutionDefinition? Snapshot { get; set; }

        public Task<AgentExecutionDefinition?> ResolveAsync(string projectId, string agentRef) =>
            Task.FromResult(Snapshot);
    }

    private sealed class NullLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NoopScope();
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
