using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Services;

public sealed class WorkflowItemTranslatorLivePromptSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateMigrated();
    private readonly WorkflowItemTranslator _translator;

    public WorkflowItemTranslatorLivePromptSpecs()
    {
        var factory = new TestDbContextFactory(_database.Options);
        var runVariablesStore = new WorkflowRunVariablesStore(factory);
        var promptResolver = new WorkflowPromptResolver(
            factory,
            new ProjectPromptStore(factory, new BuiltinPromptLoader(), new PromptTemplateEngine()));
        var variableResolver = new WorkflowVariableResolver(
            factory,
            new ProjectVariableStore(factory),
            new IssueVariableStore(factory),
            runVariablesStore);
        _translator = new WorkflowItemTranslator(
            promptResolver,
            variableResolver);
    }

    [Fact]
    public async Task DispatchReadsCurrentProjectPromptAndFallsBackToBuiltin()
    {
        const string projectId = "proj-live-prompt";
        const string runId = "wr-live-prompt";
        var run = await SeedRunAsync(projectId, runId);

        await SetPromptAsync(projectId, "first body");
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(
            [new TaskDefinition("task-1", "Task 1", "spec/task")],
            [],
            DateTimeOffset.UnixEpoch);
        run.AssignTo("runner-1", DateTimeOffset.UnixEpoch);
        run.StartTask(item.Id!, "runner-1", DateTimeOffset.UnixEpoch);
        var first = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        await SetPromptAsync(projectId, "updated body");
        var later = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        await SetPromptAsync(projectId, null);
        var fallback = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        Assert.Equal("first body", ReadPrompt(first.Variables));
        Assert.Equal("updated body", ReadPrompt(later.Variables));
        Assert.Equal("builtin body", ReadPrompt(fallback.Variables));
    }

    private async Task<WorkflowRun> SeedRunAsync(string projectId, string runId)
    {
        var definition = new WorkflowDefinition(
            [new StageDefinition("build", [new("task-1", "Task 1", "spec/task")], [])]);
        var run = WorkflowRunExtensions.Create(
            runId,
            definition,
            DateTimeOffset.UnixEpoch,
            new WorkflowRunMetadata(
                null,
                DateTimeOffset.UnixEpoch,
                 ProjectId: projectId,
                 IssueNumber: 42));
        var definitionJson = WorkflowGrainTestHelpers.SerializeProfile(definition);

        await using var db = new MohistDbContext(_database.Options);
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
            WorkflowRunId = runId,
            State = JsonSerializer.Serialize(run),
        });
        await db.SaveChangesAsync();
        return run;
    }

    private async Task SetPromptAsync(string projectId, string? body)
    {
        await using var db = new MohistDbContext(_database.Options);
        var profile = await db.ProjectWorkflowProfiles.SingleAsync(row => row.ProjectId == projectId);
        profile.Prompts = body is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["proposal"] = body };
        await db.SaveChangesAsync();
    }

    private static string? ReadPrompt(string? variables)
    {
        using var document = JsonDocument.Parse(variables!);
        return document.RootElement.GetProperty("prompts").GetProperty("proposal").GetString();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class BuiltinPromptLoader : IPromptLoader
    {
        public Dictionary<string, string> LoadAll() => new(StringComparer.Ordinal)
        {
            ["proposal"] = "builtin body",
        };
    }
}
