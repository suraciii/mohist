using System.Net;
using Mohist.Cli.TestSupport;
using Xunit;

namespace Mohist.Cli.SpecTests;

public class CliProjectOutputModeSpecs
{
    [Fact]
    public void ProjectList_Help_DescribesJsonOutputOption()
    {
        var help = CliHelpTestSupport.Render(["project", "list", "--help"]);

        var outputLine = help.Split('\n').FirstOrDefault(line => line.Contains("--output")) ?? "";
        Assert.Contains("json", outputLine);
    }

    [Fact]
    public void ProjectShow_Help_DescribesJsonOutputOption()
    {
        var help = CliHelpTestSupport.Render(["project", "show", "--help"]);

        var outputLine = help.Split('\n').FirstOrDefault(line => line.Contains("--output")) ?? "";
        Assert.Contains("json", outputLine);
    }

    [Fact]
    public async Task ProjectList_DefaultOutputMatchesExplicitJson()
    {
        var defaultRun = CliTestFactory.CreateSync(_ => RecordingHttpHandler.Json(new
        {
            success = true,
            data = new[] { new { id = "proj_1", name = "mohist-local", baseBranch = "main" } },
        }), activeProjectId: null);
        var defaultExit = await MohistCliCommands.RunAsync(
            defaultRun.Http, ["project", "list"], defaultRun.Output, defaultRun.Error, defaultRun.Fs, defaultRun.Executor);

        var explicitRun = CliTestFactory.CreateSync(_ => RecordingHttpHandler.Json(new
        {
            success = true,
            data = new[] { new { id = "proj_1", name = "mohist-local", baseBranch = "main" } },
        }), activeProjectId: null);
        var explicitExit = await MohistCliCommands.RunAsync(
            explicitRun.Http, ["project", "list", "--output", "json"], explicitRun.Output, explicitRun.Error, explicitRun.Fs, explicitRun.Executor);

        Assert.Equal(0, defaultExit);
        Assert.Equal(defaultExit, explicitExit);
        Assert.Equal(defaultRun.Output.ToString(), explicitRun.Output.ToString());
        Assert.Equal("/api/projects", defaultRun.Handler.Requests.Single().RequestUri?.PathAndQuery);
        Assert.Equal("/api/projects", explicitRun.Handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ProjectList_InvalidOutputReportsAcceptedValuesWithoutRequest()
    {
        var run = CliTestFactory.CreateSync(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            run.Http, ["project", "list", "--output", "yaml"], run.Output, run.Error, run.Fs, run.Executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(run.Handler.Requests);
        Assert.Contains("table", run.Error.ToString());
        Assert.Contains("json", run.Error.ToString());
        Assert.Contains("yaml", run.Error.ToString());
    }

    [Fact]
    public async Task ProjectShow_TableOutputDisplaysProjectDetails()
    {
        var run = CliTestFactory.CreateSync(_ => RecordingHttpHandler.Json(new
        {
            success = true,
            data = new
            {
                id = "proj_1",
                name = "mohist-local",
                baseBranch = "main",
                repositories = Array.Empty<object>(),
                createdAt = "2024-01-01T00:00:00Z",
                updatedAt = "2024-01-02T00:00:00Z",
            },
        }), activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            run.Http, ["project", "show", "mohist-local", "--output", "table"],
            run.Output, run.Error, run.Fs, run.Executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("", run.Error.ToString());
        Assert.Equal("/api/projects/mohist-local", run.Handler.Requests.Single().RequestUri?.PathAndQuery);
        Assert.Contains("mohist-local", run.Output.ToString());
        Assert.Contains("main", run.Output.ToString());
    }
}
