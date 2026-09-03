using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.L0Tests.Support;
using Mohist.Server.L0Tests.Workflow.GrainContracts;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Foundation;

[Collection("MohistDb")]
[Trait("level", "L0")]
public sealed class WorkflowVariableSpecs
{
    private static readonly FakeTimeProvider TimeProvider = new(
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowVariableSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task WorkflowDispatchKeepsTemplates()
    {
        var arrangement = await ArrangeAsync(
            "foundation-variable-template",
            new WorkflowDefinition(
            [
                new StageDefinition(
                    "build",
                    [new("task-1", "Task 1", "spec/task", With("""
                     { "path": "${{ workspace.branch }}/proposal.md" }
                    """))],
                    [])
            ]));

        var dispatch = await RenderDispatchAsync(arrangement, await CurrentWorkAsync(arrangement));

        Assert.Contains("${{ workspace.branch }}", dispatch.With);
    }

    [Fact]
    public async Task WorkflowDispatchPreservesOpaqueContextAndAddsRuntimeContext()
    {
        var arrangement = await ArrangeAsync(
            "foundation-variable-context",
            new WorkflowDefinition(
            [new StageDefinition("build", [new("task-1", "Task 1", "spec/task")], [])]));
        var variables = arrangement.Services.GetRequiredService<ProjectVariableStore>();
        await variables.PatchVariablesAsync(
            arrangement.ProjectId,
            new VariableBundle(JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement?>
            {
                ["custom"] = JsonSerializer.SerializeToElement(new { answer = 42 }),
                ["vars"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>()),
            })));

        var dispatch = await RenderDispatchAsync(arrangement, await CurrentWorkAsync(arrangement));

        Assert.NotNull(dispatch.Variables);
        using var document = JsonDocument.Parse(dispatch.Variables);
        Assert.Equal(arrangement.RunId, document.RootElement.GetProperty("workflow").GetProperty("runId").GetString());
        Assert.Equal("build", document.RootElement.GetProperty("stage").GetProperty("name").GetString());
        Assert.Equal(dispatch.WorkId, document.RootElement.GetProperty("work").GetProperty("id").GetString());
        Assert.Equal("task", document.RootElement.GetProperty("work").GetProperty("type").GetString());
        Assert.Equal(42, document.RootElement.GetProperty("vars").GetProperty("custom").GetProperty("answer").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("issue", out _));
        Assert.False(document.RootElement.TryGetProperty("project", out _));
        Assert.False(document.RootElement.TryGetProperty("artifacts", out _));
        Assert.True(document.RootElement.TryGetProperty("vars", out _));
        Assert.False(document.RootElement.GetProperty("vars").TryGetProperty("planHealthCommand", out _));
    }

    [Fact]
    public async Task GenericWorkflowCorrelationDoesNotCreateIssueDispatchReference()
    {
        var arrangement = await ArrangeAsync(
            "foundation-variable-generic",
            new WorkflowDefinition(
            [new StageDefinition("release", [new("publish", "Publish", "spec/task")], [])]),
            issueNumber: 0);

        var dispatch = await RenderDispatchAsync(arrangement, await CurrentWorkAsync(arrangement));

        Assert.Null(dispatch.Issue);
        Assert.Equal("release", dispatch.Stage);
    }

    [Fact]
    public async Task MohistWorkflowUsesExpressionInputs()
    {
        var arrangement = await ArrangeAsync(
            "foundation-variable-expression",
            MohistPlanDefinitionWithoutArtifacts());

        var proposalItem = await CurrentWorkAsync(arrangement);
        var proposal = await RenderDispatchAsync(arrangement, proposalItem);
        Assert.DoesNotContain("changeDir", proposal.With);
        Assert.NotNull(proposal.With);
        using (var proposalWith = JsonDocument.Parse(proposal.With))
        {
            Assert.False(proposalWith.RootElement.TryGetProperty("changeDir", out _));
            Assert.False(proposalWith.RootElement.TryGetProperty("artifactChangeDir", out _));
            Assert.Equal("${{ prompts.plan }}", proposalWith.RootElement.GetProperty("prompt").GetString());
        }
        Assert.NotNull(proposal.Expect);
        using (var proposalExpect = JsonDocument.Parse(proposal.Expect!))
        {
            Assert.Equal(
                "artifacts/changes/issue-${{ issue.number }}/proposal.md",
                proposalExpect.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
            Assert.False(proposalExpect.RootElement.TryGetProperty("markers", out _));
        }
        await arrangement.ReportCompletedAsync(proposalItem);

        var specsItem = await CurrentWorkAsync(arrangement);
        await arrangement.ReportCompletedAsync(specsItem);

        var designItem = await CurrentWorkAsync(arrangement);
        await arrangement.ReportCompletedAsync(designItem);

        var tasksItem = await CurrentWorkAsync(arrangement);
        await arrangement.ReportCompletedAsync(tasksItem);

        var selfReviewItem = await CurrentWorkAsync(arrangement);
        await arrangement.ReportCompletedAsync(selfReviewItem);

        var check = await RenderDispatchAsync(arrangement, await CurrentWorkAsync(arrangement));
        Assert.Equal("checks", check.WorkType);
        Assert.StartsWith("checks-", check.WorkId);
        Assert.Contains("artifacts/changes/issue-${{ issue.number }}", check.With);
        Assert.DoesNotContain("${{ artifacts.changeDir }}", check.With);
    }

    [Fact]
    public async Task MohistWorkflowUsesCoreActionsForGenericChecks()
    {
        var arrangement = await ArrangeAsync(
            "foundation-variable-core-actions",
            MohistPlanDefinitionWithoutArtifacts());

        for (var i = 0; i < 5; i++)
        {
            var item = await CurrentWorkAsync(arrangement);
            await arrangement.ReportCompletedAsync(item);
        }

        var check = await RenderDispatchAsync(arrangement, await CurrentWorkAsync(arrangement));

        Assert.Equal("checks", check.WorkType);
        Assert.Contains("mohist/plan-artifacts", check.With);
        Assert.Contains("core/marker", check.With);
        Assert.Contains("core/script", check.With);
        Assert.Contains("\"name\":\"health\"", check.With);
        Assert.Contains("\"run\":\"git diff --check\"", check.With);
    }

    private async Task<WorkflowGrainArrangement> ArrangeAsync(
        string runId,
        WorkflowDefinition definition,
        int issueNumber = 1) =>
        await WorkflowGrainArrangement.CreateAsync(_fixture, runId, definition, TimeProvider, issueNumber: issueNumber);

    private static async Task<WorkItem> CurrentWorkAsync(WorkflowGrainArrangement arrangement)
    {
        var work = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(work);
        return work!;
    }

    private static async Task<WorkDispatch> RenderDispatchAsync(
        WorkflowGrainArrangement arrangement,
        WorkItem item)
    {
        var run = await arrangement.Store.LoadAsync(arrangement.RunId)
            ?? throw new InvalidOperationException("workflow run missing");
        return await arrangement.Translator.TranslateToDispatchAsync(
            item,
            arrangement.RunId,
            run,
            arrangement.WorkerId);
    }

    private static WorkflowDefinition MohistPlanDefinitionWithoutArtifacts() =>
        new(
        [
            new StageDefinition(
                "plan",
                [
                    new("proposal", "Generate proposal", "spec/task",
                        With("""
                        { "session": "plan", "prompt": "${{ prompts.plan }}", "options": "${{ vars.agent }}" }
                        """),
                        Expect("""
                         { "files": [ { "path": "artifacts/changes/issue-${{ issue.number }}/proposal.md" } ] }
                        """)),
                    new("specs", "Write specs", "spec/task",
                        With("""
                        { "session": "plan", "prompt": "${{ prompts.specs }}", "options": "${{ vars.agent }}" }
                        """),
                        Expect("""
                         { "files": [ { "path": "artifacts/changes/issue-${{ issue.number }}/specs" } ] }
                        """)),
                    new("design", "Create design", "spec/task",
                        With("""
                        { "session": "plan", "prompt": "${{ prompts.design }}", "options": "${{ vars.agent }}" }
                        """),
                        Expect("""
                         { "files": [ { "path": "artifacts/changes/issue-${{ issue.number }}/design.md" } ] }
                        """)),
                    new("tasks", "Generate tasks", "spec/task",
                        With("""
                        { "session": "plan", "prompt": "${{ prompts.tasks }}", "options": "${{ vars.agent }}" }
                        """),
                        Expect("""
                         { "files": [ { "path": "artifacts/changes/issue-${{ issue.number }}/tasks.json" } ] }
                        """)),
                    new("self-review", "Self review", "spec/task",
                        With("""
                        { "session": "plan", "prompt": "${{ prompts.self-review }}", "options": "${{ vars.agent }}" }
                        """),
                        Expect("""
                         { "files": [ { "path": "artifacts/changes/issue-${{ issue.number }}/self-review.md" } ] }
                        """)),
                ],
                [
                    new("plan-artifacts", "Plan artifacts complete", "mohist/plan-artifacts", new Dictionary<string, JsonElement?>
                    {
                        ["changeDir"] = JsonDocument.Parse("\"artifacts/changes/issue-${{ issue.number }}\"").RootElement.Clone(),
                    }),
                    new("self-review-passed", "Self review passed", "core/marker", new Dictionary<string, JsonElement?>
                    {
                        ["path"] = JsonDocument.Parse("\"artifacts/changes/issue-${{ issue.number }}/self-review.md\"").RootElement.Clone(),
                        ["expect"] = JsonDocument.Parse("\"<promise>PASS</promise>\"").RootElement.Clone(),
                    }),
                    new("health", "Health", "core/script", new Dictionary<string, JsonElement?>
                    {
                        ["run"] = JsonDocument.Parse("\"git diff --check\"").RootElement.Clone(),
                    }),
                ])
        ]);

    private static Dictionary<string, JsonElement?> With(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(json)!;

    private static Dictionary<string, JsonElement?>? Expect(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(json)!;
}
