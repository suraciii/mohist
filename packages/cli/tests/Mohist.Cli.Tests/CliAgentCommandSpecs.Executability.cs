using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public partial class CliAgentCommandSpecs
{
    [Fact]
    public async Task AgentLaunch_NotConfiguredConflictPrintsServerExecutabilityAndFixEntry()
    {
        var handler = new RecordingHttpHandler((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = false,
                error = "This Agent is not-configured and cannot accept new work.",
                code = "agent_not_configured",
                details = new
                {
                    state = "not-configured",
                    gaps = new[]
                    {
                        new
                        {
                            code = "instructions-missing",
                            message = "Instructions are missing.",
                            nextAction = "Add instructions in Agent settings.",
                            fixEntryPoint = new { label = "Agent settings", path = "/agents/agent_x", command = "mo agent edit agent_x" },
                        },
                    },
                },
            }, HttpStatusCode.Conflict)));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "launch", "reviewer", "--prompt", "Inspect"],
            output: output,
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.NotEqual(0, exitCode);
        var errorText = error.ToString();
        Assert.Contains("not-configured", errorText, StringComparison.Ordinal);
        Assert.Contains("agent_not_configured", errorText, StringComparison.Ordinal);
        Assert.Contains("executability: not-configured", errorText, StringComparison.Ordinal);
        Assert.Contains("Instructions are missing", errorText, StringComparison.Ordinal);
        Assert.Contains("Add instructions in Agent settings", errorText, StringComparison.Ordinal);
        Assert.Contains("Agent settings", errorText, StringComparison.Ordinal);
        Assert.Contains("/agents/agent_x", errorText, StringComparison.Ordinal);
        Assert.Contains("mo agent edit agent_x", errorText, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }
}
