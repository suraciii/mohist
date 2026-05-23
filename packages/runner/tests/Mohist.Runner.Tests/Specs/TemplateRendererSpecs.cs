using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Runner.Actions;
using Mohist.Runner.Transport;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class TemplateRendererSpecs
{
    [Fact]
    public void InterpolatesContextValues()
    {
        var input = Json("""{ "path": "${{ artifacts.changeDir }}/proposal.md" }""");
        var variables = Json("""{ "artifacts": { "changeDir": "openspec/changes/42-search" } }""");

        var rendered = TemplateRenderer.Render(input, variables);

        Assert.Equal("openspec/changes/42-search/proposal.md", rendered!["path"]!.Value.GetString());
    }

    [Fact]
    public void MissingValueBecomesEmptyString()
    {
        var input = Json("""{ "path": "${{ missing.value }}/proposal.md" }""");

        var rendered = TemplateRenderer.Render(input, []);

        Assert.Equal("/proposal.md", rendered!["path"]!.Value.GetString());
    }

    [Fact]
    public void WholeExpressionPreservesJsonValue()
    {
        var input = Json("""{ "timeout": "${{ vars.timeout }}" }""");
        var variables = Json("""{ "vars": { "timeout": 600 } }""");

        var rendered = TemplateRenderer.Render(input, variables);

        Assert.Equal(JsonValueKind.Number, rendered!["timeout"]!.Value.ValueKind);
        Assert.Equal(600, rendered["timeout"]!.Value.GetInt32());
    }

    [Fact]
    public async Task WorkExecutorRendersBeforeAction()
    {
        var action = new CaptureAction();
        var executor = Executor(action);
        var work = SpecHelpers.Work("check", with: new { path = "${{ artifacts.changeDir }}/proposal.md" }, variables: new
        {
            artifacts = new { changeDir = "openspec/changes/42-search" },
            workspace = new { path = "/tmp/test" }
        });

        await executor.ExecuteAsync(work, CancellationToken.None);

        Assert.Equal("openspec/changes/42-search/proposal.md", action.Context!.With!["path"]!.Value.GetString());
    }

    [Fact]
    public async Task WorkExecutorUsesWorkspaceVariableAsWorkDir()
    {
        using var temp = new TempDir();
        var workspace = Path.Combine(temp.Path, "workspace");
        var action = new CaptureAction();
        var executor = Executor(action, workspace);
        var work = SpecHelpers.Work("task", variables: new { workspace = new { path = workspace } });

        await executor.ExecuteAsync(work, CancellationToken.None);

        Assert.Equal(workspace, action.Context!.WorkDir);
        Assert.True(Directory.Exists(workspace));
    }

    [Fact]
    public async Task WorkExecutorEnsuresWorkspaceCreatesDirectories()
    {
        using var temp = new TempDir();
        var workspace = Path.Combine(temp.Path, "worktrees", "issue-42");
        var action = new CaptureAction();
        var executor = Executor(action, null!);
        var work = SpecHelpers.Work("task", variables: new
        {
            project = new { id = "test" },
            issue = new { number = 42 },
            workspace = new { path = workspace }
        });

        await executor.ExecuteAsync(work, CancellationToken.None);

        Assert.Equal(workspace, action.Context!.WorkDir);
        Assert.True(Directory.Exists(workspace));
    }

    private static Dictionary<string, JsonElement?> Json(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(json)!;

    private static WorkExecutor Executor(IAction action, string? workspacePath = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var manager = new ActionManager(services, SpecHelpers.Logger<ActionManager>());
        manager.Register("spec/action", () => action);
        var ws = SpecHelpers.CreateWorkspaceManager(workspacePath ?? Path.GetTempPath());
        return new WorkExecutor(manager, SpecHelpers.Logger<WorkExecutor>(), ws);
    }

    private sealed class CaptureAction : IAction
    {
        public ActionContext? Context { get; private set; }

        public Task<ActionResult> ExecuteAsync(ActionContext context)
        {
            Context = context;
            return Task.FromResult(new ActionResult("success"));
        }
    }
}
