using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliAgentConnectionCommandSpecs
{
    [Fact]
    public async Task ConnectionHelp_ContainsDeliveredCommandsOnly()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http, ["agent", "connection", "--help"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var text = output.ToString();
        foreach (var command in new[] { "create", "configure", "claim-owner", "view", "list", "edit", "delete" })
            Assert.Contains(command, text, StringComparison.Ordinal);
        foreach (var command in new[] { "rotate-credentials", "transfer-owner", "enable", "disable" })
            Assert.DoesNotContain(command, text, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_ResolvesAgentAndPrintsSlackReferenceWithoutAdapterRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (request.RequestUri?.PathAndQuery.EndsWith("/agents?all=true", StringComparison.Ordinal) == true)
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new[] { new { id = "agent_1", name = "writer" } } }));
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { connection = new { id = "connection_1", botUserId = "U1" }, slackAppCreationReference = "https://api.slack.com/apps?new_app=1" },
            }));
        });

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "create", "writer", "--provider", "slack", "--workspace-team-id", "T1", "--app-id", "A1", "--bot-user-id", "U1"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains("connection_1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("https://api.slack.com/apps?new_app=1", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("/api/projects/proj_abc/slack-connections", handler.Requests[1].RequestUri?.PathAndQuery);
        Assert.Contains("agent_1", handler.Requests[1].Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configure_ProtectedFilePostsCredentialsWithoutEchoingThem()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        fs.AddFile("/tmp/slack-credentials.json", "{\"appToken\":\"xapp-secret\",\"botToken\":\"xoxb-secret\"}");

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "configure", "connection_1", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/configure", handler.Requests.Single().RequestUri?.PathAndQuery);
        Assert.Contains("xapp-secret", handler.Requests.Single().Body!, StringComparison.Ordinal);
        Assert.Contains("xoxb-secret", handler.Requests.Single().Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-secret", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-secret", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configure_DirectTokenArgumentIsRejectedBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "configure", "connection_1", "--app-token", "xapp-secret"],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("xapp-secret", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Configure_RejectsSymlinkOrWorldReadableFile(bool symlink, bool worldReadable)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        fs.AddFile("/tmp/slack-credentials.json", "{\"appToken\":\"xapp-secret\",\"botToken\":\"xoxb-secret\"}");
        fs.TreatFilesAsSymbolicLinks = symlink;
        fs.TreatFilesAsWorldReadable = worldReadable;

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "configure", "connection_1", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("xapp-secret", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-secret", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaimOwner_PrintsServerCodeOnce()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { code = "claim_once", expiresAt = "2026-07-29T10:00:00Z" } })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "claim-owner", "connection_1"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(1, Count(output.ToString(), "claim_once"));
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/claim-owner", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ViewListEditDelete_TargetConnectionEndpoints()
    {
        await AssertEndpointAsync(["view", "connection_1"], "/api/projects/proj_abc/slack-connections/connection_1", HttpMethod.Get);
        await AssertEndpointAsync(["list"], "/api/projects/proj_abc/slack-connections", HttpMethod.Get);
        await AssertEndpointAsync(["edit", "connection_1", "--bot-name", "helper"], "/api/projects/proj_abc/slack-connections/connection_1", HttpMethod.Patch);
        await AssertEndpointAsync(["delete", "connection_1"], "/api/projects/proj_abc/slack-connections/connection_1", HttpMethod.Delete);
    }

    private static async Task AssertEndpointAsync(string[] command, string path, HttpMethod method)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        var exit = await MohistCliCommands.RunAsync(http, ["agent", "connection", .. command], output, error, fs, executor);
        Assert.Equal(0, exit);
        Assert.Equal(method, handler.Requests.Single().Method);
        Assert.Equal(path, handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }
}
