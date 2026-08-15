using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public partial class CliAgentCommandSpecs
{
    [Fact]
    public async Task AgentLaunch_PreviewUsesReadOnlyEndpointAndNestedExecution()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new
            {
                agentId = "agent-1",
                agentName = "reviewer",
                runtime = "pi",
                reasoningEffort = "high",
                capabilityState = "unknown",
                matchesSavedDefinition = false,
                requestFingerprint = "fingerprint-1",
            },
        })));
        var output = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "launch", "reviewer", "--preview", "--prompt", "Inspect", "--runtime", "pi", "--reasoning-effort", "high"],
            output: output,
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/proj_123/agents/reviewer/sessions/preview", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.False(handler.Requests[0].Headers.ContainsKey("Idempotency-Key"));
        var body = JsonNode.Parse(handler.Requests[0].Body!)!.AsObject();
        Assert.Equal("Inspect", body["prompt"]?.GetValue<string>());
        Assert.Equal("pi", body["execution"]?["runtime"]?.GetValue<string>());
        Assert.Equal("high", body["execution"]?["reasoningEffort"]?.GetValue<string>());
        Assert.Contains("fingerprint-1", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentLaunch_PreviewRejectsAttachmentsBeforeUpload()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("preview must not upload or launch"));
        var error = new StringWriter();
        var fileSystem = FileSystemWithProject();
        fileSystem.AddFile("/tmp/notes.md", "hello");

        var exitCode = await RunAsync(
            handler,
            ["agent", "launch", "reviewer", "--preview", "--attach", "/tmp/notes.md"],
            error: error,
            fileSystem: fileSystem);

        Assert.Equal(2, exitCode);
        Assert.Contains("--attach cannot be used with --preview", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }
}
