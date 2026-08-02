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
        foreach (var command in new[] { "create", "configure", "rotate-credentials", "claim-owner", "transfer-owner", "disable", "enable", "view", "list", "deliveries", "resend-delivery", "edit", "delete" })
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

    [Fact]
    public async Task Edit_AccessPolicy_PostsToManageAccessWithPolicyAndMembers()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "edit", "connection_1", "--access-policy", "allowlist", "--allow-member", "U_A", "--allow-member", "U_B"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/manage-access", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("allowlist", body["accessPolicy"]!.GetValue<string>());
        Assert.Equal(new[] { "U_A", "U_B" }, body["allowMembers"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task Edit_AccessPolicyCombinedWithPresentation_PostsManageAccessThenPatches()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "edit", "connection_1", "--access-policy", "allowlist", "--bot-name", "helper"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/manage-access", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1", handler.Requests[1].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Edit_AllowMemberWithOwnerOnly_RejectedBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "edit", "connection_1", "--access-policy", "owner_only", "--allow-member", "U_A"],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("allow-member", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_AllowMemberWithAnyone_RejectedBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "edit", "connection_1", "--access-policy", "anyone", "--allow-member", "U_A", "--yes"],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("allow-member", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_InvalidPolicy_RejectedBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "edit", "connection_1", "--access-policy", "bogus"],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("access-policy", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_AnyoneWithoutYesInNonInteractiveMode_FailsWithDisclosureAndNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "edit", "connection_1", "--access-policy", "anyone"],
            output, error, fs, executor,
            terminalOverride: new CliTerminal(false));

        Assert.Equal(1, exit);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("repository-write", stderr, StringComparison.Ordinal);
        Assert.Contains("--yes", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_AnyoneWithYes_PostsToManageAccess()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "edit", "connection_1", "--access-policy", "anyone", "--yes"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/manage-access", request.RequestUri?.PathAndQuery);
        Assert.Equal("anyone", JsonNode.Parse(request.Body!)!["accessPolicy"]!.GetValue<string>());
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    [Fact]
    public async Task Deliveries_ListsRowsWithStateAndReason()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    entries = new object[]
                    {
                        new { id = "slkout_uncertain_1", state = "delivery_uncertain", kind = "terminal_result", attemptCount = 2, lastError = "claim timeout" },
                        new { id = "slkout_pending_1", state = "pending", kind = "terminal_result", attemptCount = 0, lastError = (string?)null },
                    },
                },
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "deliveries", "connection_1"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("slkout_uncertain_1", text, StringComparison.Ordinal);
        Assert.Contains("delivery_uncertain", text, StringComparison.Ordinal);
        Assert.Contains("claim timeout", text, StringComparison.Ordinal);
        Assert.Contains("slkout_pending_1", text, StringComparison.Ordinal);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/deliveries", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Deliveries_OnlyUncertain_RestrictsRows()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    entries = new object[]
                    {
                        new { id = "slkout_uncertain_1", state = "delivery_uncertain", kind = "terminal_result", attemptCount = 2, lastError = "claim timeout" },
                        new { id = "slkout_pending_1", state = "pending", kind = "terminal_result", attemptCount = 0, lastError = (string?)null },
                    },
                },
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "deliveries", "connection_1", "--only-uncertain"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("slkout_uncertain_1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("slkout_pending_1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResendDelivery_PrintsDuplicateWarningBeforePosting()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    entries = new object[]
                    {
                        new { id = "slkout_uncertain_1", state = "delivery_uncertain", kind = "terminal_result", dispatchRef = "agentjob_42", lastError = "claim timeout" },
                    },
                },
            })));

        var stdin = new StringReader("y" + Environment.NewLine);
        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "resend-delivery", "connection_1", "slkout_uncertain_1"], output, error, fs, executor,
            standardInput: stdin);

        Assert.Equal(0, exit);
        var stderr = error.ToString();
        var stdout = output.ToString();
        Assert.Contains("duplicate", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agentjob_42", stdout, StringComparison.Ordinal);
        Assert.Contains("claim timeout", stdout, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/deliveries", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/deliveries/slkout_uncertain_1/resend", handler.Requests[1].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ResendDelivery_RejectsNonUncertainRowBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    entries = new object[]
                    {
                        new { id = "slkout_pending_1", state = "pending", kind = "terminal_result", lastError = (string?)null },
                    },
                },
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "resend-delivery", "connection_1", "slkout_pending_1"], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/deliveries", handler.Requests.Single().RequestUri?.PathAndQuery);
        Assert.Contains("only Delivery uncertain", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResendDelivery_NonInteractiveWithoutYes_FailsWithDisclosure()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    entries = new object[]
                    {
                        new { id = "slkout_uncertain_1", state = "delivery_uncertain", kind = "terminal_result", lastError = "claim timeout" },
                    },
                },
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["agent", "connection", "resend-delivery", "connection_1", "slkout_uncertain_1"], output, error, fs, executor,
            terminalOverride: new CliTerminal(false));

        Assert.NotEqual(0, exit);
        Assert.Single(handler.Requests);
        Assert.Contains("--yes", error.ToString(), StringComparison.Ordinal);
    }
}
