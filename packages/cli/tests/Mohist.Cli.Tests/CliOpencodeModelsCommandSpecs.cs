using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliOpencodeModelsCommandSpecs
{
    private const string ActiveProjectId = "proj_test";

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor, MockEnvironmentVariableProvider env) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        string? activeProjectId = ActiveProjectId)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, activeProjectId);
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        return (http, handler, output, error, fs, executor, env);
    }

    private static object OpencodeModelsPayload(
        string[]? models = null,
        object? modelVariants = null)
    {
        return new
        {
            models = models ?? new[] { "anthropic/claude-sonnet", "openai/gpt-5" },
            modelVariants = modelVariants ?? new
            {
                anthropic_claude_sonnet = new[] { "default", "thinking" },
                openai_gpt_5 = new[] { "default" },
            },
        };
    }

    [Fact]
    public async Task OpencodeModels_Table_PrintsOneIdPerLineWithNoDecoration()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = OpencodeModelsPayload(),
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["opencode", "models"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/opencode/models", request.RequestUri?.PathAndQuery);
        Assert.Empty(error.ToString());

        var stdout = output.ToString();
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "anthropic/claude-sonnet", "openai/gpt-5" }, lines);
        Assert.DoesNotContain("modelVariants", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("thinking", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpencodeModels_Table_OmitsBlankLinesForEachModelId()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = OpencodeModelsPayload(),
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["opencode", "models",], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("\n\n", stdout, StringComparison.Ordinal);
        Assert.StartsWith("anthropic/claude-sonnet", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpencodeModels_SelectedJson_EmitsModelResources()
    {
        var variants = new
        {
            anthropic_claude_sonnet = new[] { "default", "thinking" },
            openai_gpt_5 = new[] { "default" },
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = OpencodeModelsPayload(modelVariants: variants),
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["opencode", "models", "--json", "id"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal($"/api/projects/{ActiveProjectId}/opencode/models", request.RequestUri?.PathAndQuery);

        var parsed = JsonNode.Parse(output.ToString().Trim()) as JsonArray;
        Assert.NotNull(parsed);
        Assert.Equal(
            new[] { "anthropic/claude-sonnet", "openai/gpt-5" },
            parsed!.Select(model => model!["id"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task OpencodeModels_NoActiveProject_FailsWithStandardError()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv(
            (_, _) => throw new InvalidOperationException("API must not be called without an active project"),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["opencode", "models"], output, error, fileSystem, executor, env);

        Assert.Equal(1, exitCode);
        Assert.Contains("No project resolved", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo project use", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OpencodeModels_NoActiveProjectWithJsonFlag_StillFailsWithStandardError()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv(
            (_, _) => throw new InvalidOperationException("API must not be called without an active project"),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["opencode", "models", "--json", "id"], output, error, fileSystem, executor, env);

        Assert.Equal(1, exitCode);
        Assert.Contains("No project resolved", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OpencodeModels_ExplicitProjectFlag_ResolvesAndCallsEndpoint()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = OpencodeModelsPayload(),
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["opencode", "models", "--project", "proj_other"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal("/api/projects/proj_other/opencode/models", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Opencode_Help_ListsModelsSubcommand()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["opencode", "--help"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("models", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpencodeModels_Help_ExplainsCopyPasteContract()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["opencode", "models", "--help"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--model", stdout, StringComparison.Ordinal);
    }
}
