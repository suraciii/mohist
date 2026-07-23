using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliWorkflowValidateSpecs
{
    private const string ValidDefinition = """
        stages:
          - stage: build
            tasks:
              - id: compile
                uses: locally-missing/action
                with:
                  arbitrary: value
            checks: []
        """;

    [Fact]
    public async Task Validate_FileInput_SucceedsWithoutHttpOrProjectResolution()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.CreateSync();
        fileSystem.AddFile("/workflow.yaml", ValidDefinition);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "validate", "--file", "/workflow.yaml"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("Workflow Definition is valid", output.ToString());
        Assert.Empty(error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Validate_StdinInput_SucceedsWithoutHttp()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "validate", "--file", "-"],
            output,
            error,
            fileSystem,
            executor,
            standardInput: new StringReader(ValidDefinition));

        Assert.Equal(0, exitCode);
        Assert.Contains("Workflow Definition is valid", output.ToString());
        Assert.Empty(error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Validate_InvalidDefinition_PrintsEveryDefinitionErrorAndFails()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.CreateSync();
        fileSystem.AddFile("/invalid.yaml", """
            unknown: true
            stages:
              - stage: build
                requiresApproval: "yes"
                tasks: []
                checks: []
            """);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "validate", "--file", "/invalid.yaml"],
            output,
            error,
            fileSystem,
            executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("unknown: unknown field 'unknown'", stderr);
        Assert.Contains("stages[0].requiresApproval", stderr);
        Assert.Equal(2, stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Empty(output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Validate_DoesNotCheckActionExistenceOrWithContracts()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.CreateSync();
        fileSystem.AddFile("/action.yaml", ValidDefinition.Replace("arbitrary: value", "unknownActionKey: true"));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "validate", "--file", "/action.yaml"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("Workflow Definition is valid", output.ToString());
        Assert.DoesNotContain("action", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }
}
