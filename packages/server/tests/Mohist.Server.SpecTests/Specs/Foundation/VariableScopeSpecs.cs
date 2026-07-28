using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Foundation;

[Collection("WorkflowExecution")]
public class WorkflowVariableSpecs : WorkflowGrainSpecs
{
    public WorkflowVariableSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task WorkflowDispatchKeepsTemplates()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("build",
                [new("task-1", "Task 1", "spec/task", With("""
                 { "path": "${{ workspace.branch }}/proposal.md" }
                """))],
                [])
        ]));

        var (work, _) = await PollWorkAnyAsync();

        Assert.Contains("${{ workspace.branch }}", work.With);
    }

    [Fact]
    public async Task WorkflowDispatchPreservesOpaqueContextAndAddsRuntimeContext()
    {
        await ClearBacklogAsync();
        var workflowId = $"wr_{Guid.NewGuid():N}";
        _workflowId = workflowId;
        _runnerId = await RegisterRunnerAsync();
        var projectId = TestProjectId(workflowId);
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, new WorkflowDefinition( [
            new StageDefinition("build",
                [new("task-1", "Task 1", "spec/task")],
                [])
        ]), projectId);
        await PatchProjectVariablesAsync(projectId, new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new Dictionary<string, JsonElement?>
            {
                ["custom"] = JsonSerializer.SerializeToElement(new { answer = 42 }),
                ["vars"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>()),
            }))));
        await workflow.StartAsync(new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: TestTime.UtcNow,
             ProjectId: projectId)));

        await EnqueueWorkflowForTestAsync(workflowId, projectId);
        var (work, _) = await PollWorkAnyAsync();

        Assert.NotNull(work.Variables);
        using var document = JsonDocument.Parse(work.Variables);
        Assert.Equal(_workflowId, document.RootElement.GetProperty("workflow").GetProperty("runId").GetString());
        Assert.Equal("build", document.RootElement.GetProperty("stage").GetProperty("name").GetString());
        Assert.Equal(work.WorkId, document.RootElement.GetProperty("work").GetProperty("id").GetString());
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
        await ClearBacklogAsync();
        var workflowId = $"wr_{Guid.NewGuid():N}";
        _workflowId = workflowId;
        _runnerId = await RegisterRunnerAsync();
        var projectId = TestProjectId(workflowId);
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

        await SeedWorkflowTemplateAsync(workflowId, new WorkflowDefinition( [
            new StageDefinition("release",
                [new("publish", "Publish", "spec/task")],
                [])
        ]), projectId);
        await workflow.StartAsync(new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: TestTime.UtcNow,
             ProjectId: projectId)));

        await EnqueueWorkflowForTestAsync(workflowId, projectId);
        var (work, _) = await PollWorkAnyAsync();

        Assert.Null(work.Issue);
        Assert.Equal("release", work.Stage);
    }

    [Fact]
    public async Task MohistWorkflowUsesExpressionInputs()
    {
        await StartWorkflowAsync(MohistPlanDefinitionWithoutArtifacts());

        var (proposal, r1) = await PollWorkAnyAsync();
        Assert.DoesNotContain("changeDir", proposal.With);
        Assert.NotNull(proposal.With);
        using (var proposalWith = JsonDocument.Parse(proposal.With))
        {
            Assert.False(proposalWith.RootElement.TryGetProperty("changeDir", out _));
            Assert.False(proposalWith.RootElement.TryGetProperty("openspecChangeDir", out _));
            Assert.Equal("${{ prompts.proposal }}", proposalWith.RootElement.GetProperty("prompt").GetString());
        }
        Assert.NotNull(proposal.Expect);
        using (var proposalExpect = JsonDocument.Parse(proposal.Expect!))
        {
             Assert.Equal("openspec/changes/issue-${{ issue.number }}/proposal.md", proposalExpect.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
            Assert.False(proposalExpect.RootElement.TryGetProperty("markers", out _));
        }
        await ReportAsync(r1, proposal.WorkId, "completed");

        var (specs, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, specs.WorkId, "completed");

        var (design, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, design.WorkId, "completed");

        var (tasks, r4) = await PollWorkAnyAsync();
        await ReportAsync(r4, tasks.WorkId, "completed");

        var (selfReview, r5) = await PollWorkAnyAsync();
        await ReportAsync(r5, selfReview.WorkId, "completed");

        var (check, _) = await PollWorkAnyAsync();
        Assert.Equal("checks", check.WorkType);
        Assert.StartsWith("checks-", check.WorkId);
         Assert.Contains("openspec/changes/issue-${{ issue.number }}", check.With);
        Assert.DoesNotContain("${{ artifacts.changeDir }}", check.With);
    }

    [Fact]
    public async Task MohistWorkflowUsesCoreActionsForGenericChecks()
    {
        await StartWorkflowAsync(MohistPlanDefinitionWithoutArtifacts());

        for (var i = 0; i < 5; i++)
        {
            var (task, runnerId) = await PollWorkAnyAsync();
            await ReportAsync(runnerId, task.WorkId, "completed");
        }

        var (check, _) = await PollWorkAnyAsync();

        Assert.Equal("checks", check.WorkType);
        Assert.Contains("mohist/openspec-artifacts", check.With);
        Assert.Contains("core/marker", check.With);
        Assert.Contains("core/script", check.With);
        Assert.Contains("\"name\":\"health\"", check.With);
        Assert.Contains("\"run\":\"git diff --check\"", check.With);
    }

    [Fact]
    public async Task MohistWorkflowDispatchesAgentWorkWithoutExecutingAgent()
    {
        await StartWorkflowAsync(Mohist.Server.Workflow.Services.WorkflowProfileCatalog.Definition);

        var (prepare, prepareRunner) = await PollWorkAnyAsync();
        Assert.Equal("task", prepare.WorkType);
        Assert.Equal("plan", prepare.Stage);
        Assert.Equal("mohist/workspace-prepare", prepare.Uses);
        await ReportAsync(prepareRunner, prepare.WorkId, "completed");

        var (proposal, _) = await PollWorkAnyAsync();

        Assert.Equal("task", proposal.WorkType);
        Assert.Equal("plan", proposal.Stage);
        Assert.Equal("mohist/opencode", proposal.Uses);
        Assert.Contains("proposal", proposal.WorkId);
        Assert.Contains("\"prompt\"", proposal.With);
        Assert.DoesNotContain("\"stage\":", proposal.With);
        Assert.DoesNotContain("\"task\":", proposal.With);
        Assert.DoesNotContain("changeDir", proposal.With);
        Assert.DoesNotContain("\"expect\"", proposal.With);
         Assert.Contains("openspec/changes/issue-${{ issue.number }}/proposal.md", proposal.Expect!);
    }

    private static WorkflowDefinition MohistPlanDefinitionWithoutArtifacts() =>
        new(
        [
            new StageDefinition("plan",
                [
                    new("proposal", "Generate proposal", "mohist/opencode",
                        With("""
                        { "session": "plan", "prompt": "${{ prompts.proposal }}", "options": "${{ vars.agent }}" }
                        """),
                        Expect("""
                         { "files": [ { "path": "openspec/changes/issue-${{ issue.number }}/proposal.md" } ] }
                        """)),
                    new("specs", "Write specs", "mohist/opencode",
                        With("""
                        { "session": "plan", "prompt": "${{ prompts.specs }}", "options": "${{ vars.agent }}" }
                        """),
                        Expect("""
                         { "files": [ { "path": "openspec/changes/issue-${{ issue.number }}/specs" } ] }
                        """)),
                    new("design", "Create design", "mohist/opencode",
                        With("""
                        { "session": "plan", "prompt": "${{ prompts.design }}", "options": "${{ vars.agent }}" }
                        """),
                        Expect("""
                         { "files": [ { "path": "openspec/changes/issue-${{ issue.number }}/design.md" } ] }
                        """)),
                    new("tasks", "Generate tasks", "mohist/opencode",
                        With("""
                        { "session": "plan", "prompt": "${{ prompts.tasks }}", "options": "${{ vars.agent }}" }
                        """),
                        Expect("""
                         { "files": [ { "path": "openspec/changes/issue-${{ issue.number }}/tasks.json" } ] }
                        """)),
                    new("self-review", "Self review", "mohist/opencode",
                        With("""
                        { "session": "plan", "prompt": "${{ prompts.self-review }}", "options": "${{ vars.agent }}" }
                        """),
                        Expect("""
                         { "files": [ { "path": "openspec/changes/issue-${{ issue.number }}/self-review.md" } ] }
                        """)),
                ],
                [
                     new("plan-artifacts", "Plan artifacts complete", "mohist/openspec-artifacts", new Dictionary<string, JsonElement?> { ["changeDir"] = JsonDocument.Parse("\"openspec/changes/issue-${{ issue.number }}\"").RootElement.Clone() }),
                     new("self-review-passed", "Self review passed", "core/marker", new Dictionary<string, JsonElement?> { ["path"] = JsonDocument.Parse("\"openspec/changes/issue-${{ issue.number }}/self-review.md\"").RootElement.Clone(), ["expect"] = JsonDocument.Parse("\"<promise>PASS</promise>\"").RootElement.Clone() }),
                    new("health", "Health", "core/script", new Dictionary<string, JsonElement?> { ["run"] = JsonDocument.Parse("\"git diff --check\"").RootElement.Clone() }),
                ])
        ]);

    private static new Dictionary<string, JsonElement?> With(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(json)!;

    private static Dictionary<string, JsonElement?>? Expect(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(json)!;
}
