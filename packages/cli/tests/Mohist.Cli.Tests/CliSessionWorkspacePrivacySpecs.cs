using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliSessionWorkspacePrivacySpecs
{
    private const string ActiveProjectId = "proj_test";
    private const string StableSessionId = "sess_123";

    [Fact]
    public async Task SessionShow_DoesNotRenderWorkspacePathFromContextRefs()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            (_, _) =>
                Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = StableSessionId,
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
                })),
            ActiveProjectId);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "view", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        var stdout = output.ToString();
        Assert.Contains("issue #7", stdout, StringComparison.Ordinal);
        Assert.Contains("workspace: issue-7", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("/srv/private/worktree", stdout, StringComparison.Ordinal);
    }
}
