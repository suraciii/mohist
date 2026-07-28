using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests.Api;

public sealed class EpicCliDescriptionInputSpecs
{
    [Fact]
    public async Task EpicCreate_Help_ExplainsDescriptionFileAndStdin()
    {
        var (handler, http, output, error, fileSystem, executor) = CreateEnvironment();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["epic", "create", "--help"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var help = output.ToString();
        Assert.Contains("--description-file", help, StringComparison.Ordinal);
        Assert.Contains("mutually exclusive", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stdin", help, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EpicEdit_Help_ExplainsDescriptionFileAndStdin()
    {
        var (handler, http, output, error, fileSystem, executor) = CreateEnvironment();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["epic", "edit", "8", "--help"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var help = output.ToString();
        Assert.Contains("--description-file", help, StringComparison.Ordinal);
        Assert.Contains("mutually exclusive", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stdin", help, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EpicCreate_DescriptionFile_SendsContents()
    {
        var (handler, http, output, error, fileSystem, executor) = CreateEnvironment();
        fileSystem.AddFile("epic.md", "# milestone\nLong description.\n");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["epic", "create", "Milestone", "--description-file", "epic.md"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!.AsObject();
        Assert.Equal("# milestone\nLong description.\n", body["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task EpicCreate_DescriptionFileDash_ReadsStdinAndSendsContents()
    {
        var (handler, http, output, error, fileSystem, executor) = CreateEnvironment();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["epic", "create", "Milestone", "--description-file", "-"],
            output,
            error,
            fileSystem,
            executor,
            standardInput: new StringReader("piped description\nmore"));

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!.AsObject();
        Assert.Equal("piped description\nmore", body["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task EpicEdit_DescriptionFile_SendsContents()
    {
        var (handler, http, output, error, fileSystem, executor) = CreateEnvironment();
        fileSystem.AddFile("epic.md", "Updated description.\n");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["epic", "edit", "8", "--description-file", "epic.md"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!.AsObject();
        Assert.Equal("Updated description.\n", body["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task EpicEdit_DescriptionFileDash_ReadsStdinAndSendsContents()
    {
        var (handler, http, output, error, fileSystem, executor) = CreateEnvironment();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["epic", "edit", "8", "--description-file", "-"],
            output,
            error,
            fileSystem,
            executor,
            standardInput: new StringReader("stdin description"));

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!.AsObject();
        Assert.Equal("stdin description", body["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task EpicCreate_DescriptionAndDescriptionFile_ExitsUsageErrorWithoutRequest()
    {
        var (handler, http, output, error, fileSystem, executor) = CreateEnvironment();
        fileSystem.AddFile("epic.md", "from file");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["epic", "create", "Milestone", "--description", "inline", "--description-file", "epic.md"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--description", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--description-file", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mutually exclusive", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EpicEdit_DescriptionAndDescriptionFile_ExitsUsageErrorWithoutRequest()
    {
        var (handler, http, output, error, fileSystem, executor) = CreateEnvironment();
        fileSystem.AddFile("epic.md", "from file");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["epic", "edit", "8", "--description", "inline", "--description-file", "epic.md"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--description", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--description-file", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mutually exclusive", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateEnvironment()
    {
        return CliTestFactory.Create((_, _) => Task.FromResult(
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { number = 8, title = "Milestone", description = "description", status = "idle", priority = "p2" },
            })));
    }
}
