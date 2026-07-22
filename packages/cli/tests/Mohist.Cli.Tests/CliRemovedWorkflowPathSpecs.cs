using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliRemovedWorkflowPathSpecs
{
    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("retry")]
    [InlineData("rerun")]
    [InlineData("rerun-from-stage")]
    [InlineData("force-stop")]
    [InlineData("resume")]
    [InlineData("stop")]
    public async Task RemovedIssueControlVerb_FailsWithoutHttp(string verb)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", verb, "42"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueFeedbackPath_FailsWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "feedback", "list", "42"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WorkflowGroup_FailsWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "approve", "wr_abc"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RootHelpListsRunAndOmitsWorkflow()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(http, ["--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("\n  run ", output.ToString());
        Assert.DoesNotContain("\n  workflow ", output.ToString());
        Assert.Empty(handler.Requests);
    }
}
