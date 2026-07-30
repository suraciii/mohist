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
        foreach (var command in new[] { "create", "configure", "rotate-credentials", "claim-owner", "transfer-owner", "disable", "enable", "view", "list", "edit", "delete" })
            Assert.Contains(command, text, StringComparison.Ordinal);
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
            ["agent", "connection", "create", "writer", "--provider", "slack"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains("connection_1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("https://api.slack.com/apps?new_app=1", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("/api/projects/proj_abc/slack-connections", handler.Requests[1].RequestUri?.PathAndQuery);
        Assert.Contains("agent_1", handler.Requests[1].Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("workspaceTeamId", handler.Requests[1].Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("appId", handler.Requests[1].Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("botUserId", handler.Requests[1].Body!, StringComparison.Ordinal);
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

    [Fact]
    public async Task RotateCredentials_ProtectedFilePostsToRotationEndpointWithoutEchoingThem()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        fs.AddFile("/tmp/slack-credentials.json", "{\"appToken\":\"xapp-secret\",\"botToken\":\"xoxb-secret\"}");

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "rotate-credentials", "connection_1", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/rotate-credentials", handler.Requests.Single().RequestUri?.PathAndQuery);
        Assert.Contains("xapp-secret", handler.Requests.Single().Body!, StringComparison.Ordinal);
        Assert.Contains("xoxb-secret", handler.Requests.Single().Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-secret", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-secret", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotateCredentials_DirectTokenArgumentIsRejectedBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "rotate-credentials", "connection_1", "xapp-secret"],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("xapp-secret", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RotateCredentials_RejectsSymlinkOrWorldReadableFile(bool symlink, bool worldReadable)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        fs.AddFile("/tmp/slack-credentials.json", "{\"appToken\":\"xapp-secret\",\"botToken\":\"xoxb-secret\"}");
        fs.TreatFilesAsSymbolicLinks = symlink;
        fs.TreatFilesAsWorldReadable = worldReadable;

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "rotate-credentials", "connection_1", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("xapp-secret", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-secret", output.ToString(), StringComparison.Ordinal);
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
        await AssertEndpointAsync(["view", "connection_1"], "/api/projects/proj_abc/slack-connections/connection_1/diagnostic", HttpMethod.Get);
        await AssertEndpointAsync(["list"], "/api/projects/proj_abc/slack-connections", HttpMethod.Get);
        await AssertEndpointAsync(["edit", "connection_1", "--bot-name", "helper"], "/api/projects/proj_abc/slack-connections/connection_1", HttpMethod.Patch);
        await AssertEndpointAsync(["delete", "connection_1"], "/api/projects/proj_abc/slack-connections/connection_1", HttpMethod.Delete);
    }

    [Fact]
    public async Task TransferOwner_PrintsCodeAndExpiryAndTargetsTransferEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { code = "transfer_once", expiresAt = "2026-07-30T10:00:00Z" },
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "transfer-owner", "connection_1"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/transfer-owner", handler.Requests.Single().RequestUri?.PathAndQuery);
        Assert.Contains("transfer_once", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("2026-07-30T10:00:00Z", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disable", "/disable")]
    [InlineData("enable", "/enable")]
    public async Task DesiredStateCommands_TargetCorrectEndpoints(string command, string suffix)
    {
        var desiredState = command == "disable" ? "disabled" : "enabled";
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { desiredState },
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", command, "connection_1"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
        Assert.Equal($"/api/projects/proj_abc/slack-connections/connection_1{suffix}", handler.Requests.Single().RequestUri?.PathAndQuery);
        Assert.Contains(desiredState, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task View_RendersDiagnosticSummaryAndSupportingFacts()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    primaryState = "owner_unavailable",
                    reason = "The current Slack Owner is no longer an eligible workspace member.",
                    nextAction = "Transfer ownership.",
                    facts = new
                    {
                        setupProgress = "complete",
                        desiredState = "enabled",
                        connectionHealth = "healthy",
                        ownerAvailability = "unavailable",
                        identity = new { verificationStatus = "verified" },
                    },
                },
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "view", "connection_1"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains("owner_unavailable", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Transfer ownership.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Supporting facts", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ownerAvailability: unavailable", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/diagnostic", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task List_RendersStoredPrimaryStateWithoutDiagnosticProbe()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[]
                {
                    new { id = "connection_1", botName = "helper", setupProgress = "complete", desiredState = "disabled", connectionHealth = "healthy", agentReadiness = "ready" },
                    new { id = "connection_2", botName = "writer", setupProgress = "complete", desiredState = "enabled", connectionHealth = "unhealthy", healthReason = "Slack rejected the Bot token", agentReadiness = "ready" },
                },
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "list"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("primary state", text, StringComparison.Ordinal);
        Assert.Contains("disabled", text, StringComparison.Ordinal);
        Assert.Contains("credentials_invalid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic", handler.Requests.Select(r => r.RequestUri?.PathAndQuery).Single()!, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
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
