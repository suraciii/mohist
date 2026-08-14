using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliSessionWorkspacePrivacySpecs
{
    [Fact]
    public async Task SessionShow_DoesNotRenderWorkspacePathFromContextRefs()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "sess_123",
                    source = "agent-launch",
                    activity = "idle",
                    agentId = "agent_456",
                    agentName = "reviewer",
                    createdAt = "2026-06-26T10:00:00Z",
                    lastActivityAt = "2026-06-26T10:05:00Z",
                    contextRefs = new
                    {
                        issueNumber = 7,
                        repository = "owner/repo",
                        workspaceName = "issue-7",
                        workspacePath = "/srv/private/worktree",
                    },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "view", "sess_123"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        Assert.Contains("issue #7", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("workspace: issue-7", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/srv/private/worktree", output.ToString(), StringComparison.Ordinal);
    }
}
