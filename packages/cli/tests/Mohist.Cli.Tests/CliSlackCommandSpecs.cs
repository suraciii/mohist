using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliSlackCommandSpecs
{
    [Fact]
    public async Task SlackHelp_ContainsMappedCommandsAndSetup()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http, ["slack", "--help"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var text = output.ToString();
        foreach (var command in new[] { "setup", "status", "install-agent", "list", "view", "claim-owner", "edit", "transfer-owner", "enable", "disable", "remove-binding", "permanent-delete", "deliveries", "resend-delivery", "clear-gap", "reconcile-create", "reconcile-delete", "message" })
            Assert.Contains(command, text, StringComparison.Ordinal);
        Assert.DoesNotContain("agent connection", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("setup", "--workspace-team", "--configuration-token-file", "--credentials-file")]
    [InlineData("install-agent", "--workspace-team", "--credentials-file", "--project")]
    [InlineData("status", "--workspace-team")]
    public async Task SlackWizardAndStatusHelp_ExposeTheirLeafInputsWithoutHttp(
        string command,
        params string[] expectedOptions)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", command, "--help"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exit);
        var text = output.ToString();
        foreach (var option in expectedOptions)
            Assert.Contains(option, text, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("create", "writer")]
    [InlineData("configure", "connection_1")]
    [InlineData("configure-manager", "--workspace-team", "T_W")]
    [InlineData("create-child-app", "connection_1")]
    [InlineData("rotate-credentials", "connection_1")]
    [InlineData("delete", "connection_1")]
    public async Task RetiredCommands_AreRejectedBeforeHttp(params string[] command)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http, ["slack", .. command], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task LegacyAgentConnectionCommand_IsRejectedWithoutHttpRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["agent", "connection", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Setup_NonInteractiveWithoutWorkspace_FailsWithUsageBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http, ["slack", "setup"], output, error, fs, executor);

        Assert.Equal(2, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("--workspace-team", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("setup", "--manager-bot-token", "manager-credential-fixture")]
    [InlineData("setup", "xapp-token-literal")]
    [InlineData("install-agent", "writer", "xoxb-token-literal")]
    [InlineData("install-agent", "writer", "--app-token", "xapp-token-literal")]
    [InlineData("install-agent", "writer", "--json", "xoxb-token-literal")]
    public async Task WizardCommands_RefuseDirectTokenLiteralsBeforeHttp(params string[] command)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http, ["slack", .. command], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("xapp-token-literal", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-token-literal", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-token-literal", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-token-literal", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("manager-credential-fixture", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("manager-credential-fixture", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAgent_JsonFieldList_IsNotRefusedAsTokenLiteral()
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            if (path == "/api/slack-manager/setup/progress?workspaceTeamId=T_W")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("ready"),
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    enrollmentId = "enrollment_1",
                    workspaceTeamId = "T_W",
                    connection = new { id = "connection_1", botName = "writer-bot" },
                    agentApp = new { id = "agent_app_1", runtimeCredentialValidationState = "not_provided" },
                    nextAction = "ready",
                    errorClass = (string?)null,
                },
            }));
        });

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W", "--json", "connection,nextAction"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        var projected = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal("connection_1", projected["connection"]!["id"]!.GetValue<string>());
        Assert.Equal("ready", projected["nextAction"]!.GetValue<string>());
        Assert.Equal(2, projected.Count);
    }

    [Fact]
    public async Task Setup_ConfigurationAndRuntimeCredentials_ArePostedInSequenceWithoutEcho()
    {
        const string configurationToken = "configuration-token-fixture-0123456789";
        const string refreshToken = "configuration-refresh-token-fixture";
        const string botToken = "xoxb-runtime-bot-fixture-0123456789";
        const string appToken = "xapp-runtime-app-fixture-0123456789";
        var env = OperatorEnv();
        var progressCalls = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (request.RequestUri?.PathAndQuery == "/api/slack-manager/setup/progress?workspaceTeamId=T_W")
            {
                progressCalls++;
                return Task.FromResult(progressCalls switch
                {
                    1 => RecordingHttpHandler.Json(new { success = true, data = EnrollmentProgress("supply_configuration", phase: "not_started") }),
                    2 => RecordingHttpHandler.Json(new { success = true, data = EnrollmentProgress("supply_runtime_credentials", phase: "awaiting_install") }),
                    _ => RecordingHttpHandler.Json(new { success = true, data = EnrollmentProgress("report_socket_hello", phase: "awaiting_socket_validation") }),
                });
            }

            if (request.RequestUri?.PathAndQuery == "/api/slack-manager/setup/configuration")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("supply_runtime_credentials", phase: "awaiting_install"),
                }));

            if (request.RequestUri?.PathAndQuery == "/api/slack-manager/setup/runtime-credentials")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("report_socket_hello", phase: "awaiting_socket_validation"),
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        });
        fs.AddFile("/tmp/configuration-token.json", $"{{\"configurationToken\":\"{configurationToken}\",\"configurationRefreshToken\":\"{refreshToken}\"}}");
        fs.AddFile("/tmp/slack-credentials.json", $"{{\"botToken\":\"{botToken}\",\"appToken\":\"{appToken}\"}}");

        var exit = await MohistCliCommands.RunAsync(
            http,
            [
                "slack", "setup",
                "--workspace-team", "T_W",
                "--configuration-token-file", "/tmp/configuration-token.json",
                "--credentials-file", "/tmp/slack-credentials.json",
            ],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        Assert.Equal(
            [
                "/api/slack-manager/setup/progress?workspaceTeamId=T_W",
                "/api/slack-manager/setup/configuration",
                "/api/slack-manager/setup/progress?workspaceTeamId=T_W",
                "/api/slack-manager/setup/runtime-credentials",
                "/api/slack-manager/setup/progress?workspaceTeamId=T_W",
            ],
            handler.Requests.Select(request => request.RequestUri?.PathAndQuery ?? string.Empty).ToArray());

        foreach (var request in handler.Requests)
            Assert.Equal(OperatorToken, Assert.Single(request.Headers[OperatorCredentialProvider.HeaderName]));

        var configurationBody = JsonNode.Parse(handler.Requests[1].Body!)!;
        Assert.Equal("T_W", configurationBody["workspaceTeamId"]!.GetValue<string>());
        Assert.Equal(configurationToken, configurationBody["configurationAccessToken"]!.GetValue<string>());
        Assert.Equal(refreshToken, configurationBody["configurationRefreshToken"]!.GetValue<string>());

        var runtimeBody = JsonNode.Parse(handler.Requests[3].Body!)!;
        Assert.Equal(botToken, runtimeBody["botToken"]!.GetValue<string>());
        Assert.Equal(appToken, runtimeBody["appLevelToken"]!.GetValue<string>());

        var stdout = output.ToString();
        Assert.Contains("report_socket_hello", stdout, StringComparison.Ordinal);
        Assert.Contains("https://api.slack.com/apps/A_1/oauth", stdout, StringComparison.Ordinal);
        Assert.Contains("Configuration credentials accepted for T_W.", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Runtime credentials accepted for T_W.", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(configurationToken, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(configurationToken, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(appToken, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(appToken, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_ResumesFromRuntimeCredentialsStepWithoutReSupplyingConfiguration()
    {
        const string botToken = "xoxb-runtime-bot-fixture-0123456789";
        const string appToken = "xapp-runtime-app-fixture-0123456789";
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
            request.RequestUri?.PathAndQuery == "/api/slack-manager/setup/progress?workspaceTeamId=T_W"
                ? Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("supply_runtime_credentials", phase: "awaiting_install"),
                }))
                : Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("ready"),
                })));
        fs.AddFile("/tmp/slack-credentials.json", $"{{\"botToken\":\"{botToken}\",\"appToken\":\"{appToken}\"}}");

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        Assert.DoesNotContain(handler.Requests, request =>
            string.Equals(request.RequestUri?.PathAndQuery, "/api/slack-manager/setup/configuration", StringComparison.Ordinal));
        Assert.Contains("Runtime credentials accepted for T_W.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_ReadyWorkspacePrintsSummaryWithoutMutation()
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = EnrollmentProgress("ready"),
            })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.Contains("Slack workspace T_W is ready.", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Claim the Mohist App", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_ReadyWithCredentialsFile_RotatesViaRuntimeCredentialsRoute()
    {
        const string rotatedBotToken = "xoxb-rotated-bot-fixture-0123456789";
        const string rotatedAppToken = "xapp-rotated-app-fixture-0123456789";
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
            request.RequestUri?.PathAndQuery == "/api/slack-manager/setup/progress?workspaceTeamId=T_W"
                ? Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("ready"),
                }))
                : Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("ready"),
                })));
        fs.AddFile("/tmp/slack-credentials.json", $"{{\"botToken\":\"{rotatedBotToken}\",\"appToken\":\"{rotatedAppToken}\"}}");

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        var credentialPosts = handler.Requests
            .Where(request => request.RequestUri?.PathAndQuery == "/api/slack-manager/setup/runtime-credentials")
            .ToList();
        Assert.Single(credentialPosts);
        Assert.Equal(rotatedBotToken, JsonNode.Parse(credentialPosts[0].Body!)!["botToken"]!.GetValue<string>());
        Assert.Equal(rotatedAppToken, JsonNode.Parse(credentialPosts[0].Body!)!["appLevelToken"]!.GetValue<string>());
        Assert.Contains("Runtime credentials rotated for T_W.", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedBotToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedBotToken, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedAppToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedAppToken, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--manager-app-id", "A_1")]
    [InlineData("--manager-bot-user-id", "U_1")]
    [InlineData("--manager-credential-ref", "ref_1")]
    public async Task Setup_RefusesLegacyManagerFactsBeforeHttp(string option, string value)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W", option, value],
            output, error, fs, executor);

        Assert.Equal(2, exit);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(value, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_MissingConfigurationTokenFileAtConfigurationStep_FailsWithUsageBeforePost()
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = EnrollmentProgress("supply_configuration", phase: "configuration_required"),
            })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W"],
            output, error, fs, executor, env);

        Assert.Equal(2, exit);
        Assert.DoesNotContain(handler.Requests, request =>
            string.Equals(request.RequestUri?.PathAndQuery, "/api/slack-manager/setup/configuration", StringComparison.Ordinal));
        var stderr = error.ToString();
        Assert.Contains("--configuration-token-file", stderr, StringComparison.Ordinal);
        Assert.Contains("Re-run `mo slack setup`", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_ConfigurationTokenFile_ShapeValidatedAndPostedWithoutEcho()
    {
        const string configurationToken = "configuration-token-fixture";
        const string refreshToken = "refresh-fixture";
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
            request.RequestUri?.PathAndQuery == "/api/slack-manager/setup/progress?workspaceTeamId=T_W"
                ? Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("supply_configuration", phase: "configuration_required"),
                }))
                : Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("ready"),
                })));
        fs.AddFile("/tmp/configuration-token.json", $"{{\"configurationToken\":\"{configurationToken}\",\"configurationRefreshToken\":\"{refreshToken}\"}}");

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W", "--configuration-token-file", "/tmp/configuration-token.json"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        Assert.Equal(3, handler.Requests.Count);
        var configurationRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, configurationRequest.Method);
        Assert.Equal("/api/slack-manager/setup/configuration", configurationRequest.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(configurationRequest.Body!)!;
        Assert.Equal(configurationToken, body["configurationAccessToken"]!.GetValue<string>());
        Assert.Equal(refreshToken, body["configurationRefreshToken"]!.GetValue<string>());
        Assert.Contains("Configuration token pair accepted", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(configurationToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(configurationToken, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Setup_ConfigurationTokenFile_RejectsSymlinkOrWorldReadableFileBeforeHttp(bool symlink, bool worldReadable)
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = EnrollmentProgress("supply_configuration", phase: "configuration_required"),
            })));
        fs.AddFile("/tmp/configuration-token.json", "{\"configurationToken\":\"ct-secret-fixture\",\"configurationRefreshToken\":\"cr-secret-fixture\"}");
        fs.TreatFilesAsSymbolicLinks = symlink;
        fs.TreatFilesAsWorldReadable = worldReadable;

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W", "--configuration-token-file", "/tmp/configuration-token.json"],
            output, error, fs, executor, env);

        Assert.NotEqual(0, exit);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, request =>
            string.Equals(request.RequestUri?.PathAndQuery, "/api/slack-manager/setup/configuration", StringComparison.Ordinal));
        Assert.DoesNotContain("ct-secret-fixture", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ct-secret-fixture", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_SelectedJson_ProjectsRequestedFieldsAfterWizard()
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = EnrollmentProgress("report_socket_hello", phase: "awaiting_socket_validation"),
            })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W", "--json", "installUrl,nextAction"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        var projected = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal("https://api.slack.com/apps/A_1/oauth", projected["installUrl"]!.GetValue<string>());
        Assert.Equal("report_socket_hello", projected["nextAction"]!.GetValue<string>());
        Assert.Equal(2, projected.Count);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task InstallAgent_NonInteractiveWithoutWorkspace_FailsAfterAgentResolve()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[] { new { id = "agent_1", name = "writer" } },
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer"], output, error, fs, executor);

        Assert.Equal(2, exit);
        Assert.Single(handler.Requests);
        Assert.Contains("--workspace-team", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAgent_PostsUnifiedRouteAndStopsAtCredentialsStep()
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            if (path == "/api/slack-manager/setup/progress?workspaceTeamId=T_W")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("ready"),
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    enrollmentId = "enrollment_1",
                    workspaceTeamId = "T_W",
                    connection = new { id = "connection_1", botName = "writer-bot" },
                    agentApp = new { id = "agent_app_1", runtimeCredentialValidationState = "not_provided" },
                    nextAction = "provide_credentials",
                    errorClass = (string?)null,
                },
            }));
        });

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W"],
            output, error, fs, executor, env);

        Assert.Equal(2, exit);
        Assert.Equal(
            [
                "/api/projects/proj_abc/agents?all=true",
                "/api/slack-manager/setup/progress?workspaceTeamId=T_W",
                "/api/projects/proj_abc/slack-manager/install-agent",
            ],
            handler.Requests.Select(request => request.RequestUri?.PathAndQuery ?? string.Empty).ToArray());

        var installRequest = handler.Requests[2];
        Assert.Equal(HttpMethod.Post, installRequest.Method);
        Assert.Equal(OperatorToken, Assert.Single(installRequest.Headers[OperatorCredentialProvider.HeaderName]));
        var installBody = JsonNode.Parse(installRequest.Body!)!;
        Assert.Equal("enrollment_1", installBody["enrollmentId"]!.GetValue<string>());
        Assert.Equal("agent_1", installBody["agentId"]!.GetValue<string>());

        Assert.Equal(string.Empty, output.ToString());
        var stderr = error.ToString();
        Assert.Contains("needs the runtime credentials", stderr, StringComparison.Ordinal);
        Assert.Contains("--credentials-file", stderr, StringComparison.Ordinal);
        Assert.Contains("mo slack install-agent", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, request =>
            (request.RequestUri?.PathAndQuery ?? string.Empty).Contains("/slack-manager/apps", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request =>
            (request.RequestUri?.PathAndQuery ?? string.Empty).Contains("create", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request =>
            (request.RequestUri?.PathAndQuery ?? string.Empty).Contains("begin-authorization", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request =>
            (request.RequestUri?.PathAndQuery ?? string.Empty).Contains("authorization-progress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAgent_ResumesExistingInstallationWithoutCreatingSecondApp()
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            if (path == "/api/slack-manager/setup/progress?workspaceTeamId=T_W")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("ready"),
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    enrollmentId = "enrollment_1",
                    workspaceTeamId = "T_W",
                    connection = new { id = "connection_1", botName = "writer-bot" },
                    agentApp = new { id = "agent_app_1", runtimeCredentialValidationState = "not_provided" },
                    nextAction = "wait_for_operation",
                    errorClass = (string?)null,
                },
            }));
        });

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request =>
            (request.RequestUri?.PathAndQuery ?? string.Empty).Contains("/slack-manager/apps", StringComparison.Ordinal));
        var stdout = output.ToString();
        Assert.Contains("in progress", stdout, StringComparison.Ordinal);
        Assert.Contains("wait_for_operation", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAgent_Ready_PrintsConnectionAndBotSummary()
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            if (path == "/api/slack-manager/setup/progress?workspaceTeamId=T_W")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("ready"),
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    enrollmentId = "enrollment_1",
                    workspaceTeamId = "T_W",
                    connection = new { id = "connection_1", botName = "writer-bot" },
                    agentApp = new { id = "agent_app_1", runtimeCredentialValidationState = "verified" },
                    nextAction = "ready",
                    errorClass = (string?)null,
                },
            }));
        });

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        var stdout = output.ToString();
        Assert.Contains("installed and ready in workspace T_W", stdout, StringComparison.Ordinal);
        Assert.Contains("connection_1", stdout, StringComparison.Ordinal);
        Assert.Contains("writer-bot", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAgent_AgentNotFound_FailsBeforeWorkspacePhase()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Array.Empty<object>(),
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "missing", "--workspace-team", "T_W"],
            output, error, fs, executor);

        Assert.Equal(1, exit);
        Assert.Single(handler.Requests);
        Assert.Contains("missing", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAgent_CredentialsFile_AreSubmittedToUnifiedCredentialsRouteWithoutEcho()
    {
        const string appToken = "xapp-secret-fixture";
        const string botToken = "xoxb-secret-fixture";
        var env = OperatorEnv();
        var installCalls = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            if (path == "/api/slack-manager/setup/progress?workspaceTeamId=T_W")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("ready"),
                }));

            if (path.EndsWith("/slack-manager/install-agent/credentials", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { accepted = true, runtimeCredentialValidationState = "candidate", errorClass = (string?)null },
                }));

            installCalls++;
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    enrollmentId = "enrollment_1",
                    workspaceTeamId = "T_W",
                    connection = new { id = "connection_1", botName = "writer" },
                    agentApp = new
                    {
                        id = "agent_app_1",
                        runtimeCredentialValidationState = installCalls == 1 ? "not_provided" : "candidate",
                    },
                    nextAction = "provide_credentials",
                    errorClass = (string?)null,
                },
            }));
        });
        fs.AddFile("/tmp/slack-credentials.json", $"{{\"appToken\":\"{appToken}\",\"botToken\":\"{botToken}\"}}");

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        Assert.Equal(
            [
                "/api/projects/proj_abc/agents?all=true",
                "/api/slack-manager/setup/progress?workspaceTeamId=T_W",
                "/api/projects/proj_abc/slack-manager/install-agent",
                "/api/projects/proj_abc/slack-manager/install-agent/credentials",
                "/api/projects/proj_abc/slack-manager/install-agent",
            ],
            handler.Requests.Select(request => request.RequestUri?.PathAndQuery ?? string.Empty).ToArray());

        var credentialsRequest = handler.Requests[3];
        Assert.Equal(HttpMethod.Post, credentialsRequest.Method);
        var credentialsBody = JsonNode.Parse(credentialsRequest.Body!)!;
        Assert.Equal("agent_app_1", credentialsBody["agentAppId"]!.GetValue<string>());
        Assert.Equal(botToken, credentialsBody["botToken"]!.GetValue<string>());
        Assert.Equal(appToken, credentialsBody["appLevelToken"]!.GetValue<string>());

        var stdout = output.ToString();
        Assert.Contains("waiting for the Slack connection", stdout, StringComparison.Ordinal);
        Assert.Contains("Socket hello", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(appToken, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(appToken, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InstallAgent_CredentialsFile_RejectsSymlinkOrWorldReadableFileWithoutPosting(bool symlink, bool worldReadable)
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            if (path == "/api/slack-manager/setup/progress?workspaceTeamId=T_W")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("ready"),
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    enrollmentId = "enrollment_1",
                    workspaceTeamId = "T_W",
                    connection = new { id = "connection_1", botName = "writer" },
                    agentApp = new { id = "agent_app_1", runtimeCredentialValidationState = "not_provided" },
                    nextAction = "provide_credentials",
                    errorClass = (string?)null,
                },
            }));
        });
        fs.AddFile("/tmp/slack-credentials.json", "{\"appToken\":\"xapp-secret\",\"botToken\":\"xoxb-secret\"}");
        fs.TreatFilesAsSymbolicLinks = symlink;
        fs.TreatFilesAsWorldReadable = worldReadable;

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor, env);

        Assert.NotEqual(0, exit);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request =>
            (request.RequestUri?.PathAndQuery ?? string.Empty).EndsWith("/install-agent/credentials", StringComparison.Ordinal));
        Assert.DoesNotContain("xapp-secret", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_MissingCredentialsFile_FailsWithInstallUrlAndNextStepHint()
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = EnrollmentProgress("supply_runtime_credentials", phase: "awaiting_install"),
            })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W"],
            output, error, fs, executor, env);

        Assert.Equal(2, exit);
        Assert.Single(handler.Requests);
        Assert.Equal(string.Empty, output.ToString());
        var stderr = error.ToString();
        Assert.Contains("supply_runtime_credentials", stderr, StringComparison.Ordinal);
        Assert.Contains("https://api.slack.com/apps/A_1/oauth", stderr, StringComparison.Ordinal);
        Assert.Contains("--credentials-file", stderr, StringComparison.Ordinal);
        Assert.Contains("mo slack setup", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_WaitingForSocketHello_PrintsUniqueNextStepWithoutFakingValidation()
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = EnrollmentProgress("report_socket_hello", phase: "awaiting_socket_validation"),
            })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
        var stdout = output.ToString();
        Assert.Contains("report_socket_hello", stdout, StringComparison.Ordinal);
        Assert.Contains("Socket hello", stdout, StringComparison.Ordinal);
        Assert.Contains("mo slack setup", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_RuntimeCredentialMismatch_ReportsErrorClassAndExits1()
    {
        const string botToken = "xoxb-mismatch-bot-fixture-0123456789";
        const string appToken = "xapp-mismatch-app-fixture-0123456789";
        var env = OperatorEnv();
        var progressCalls = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (request.RequestUri?.PathAndQuery == "/api/slack-manager/setup/progress?workspaceTeamId=T_W")
            {
                progressCalls++;
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = progressCalls == 1
                        ? EnrollmentProgress("supply_runtime_credentials", phase: "awaiting_install")
                        : EnrollmentProgress("supply_runtime_credentials", phase: "failed", errorClass: "runtime_credential_mismatch"),
                }));
            }

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = EnrollmentProgress("supply_runtime_credentials", phase: "failed", errorClass: "runtime_credential_mismatch"),
            }));
        });
        fs.AddFile("/tmp/slack-credentials.json", $"{{\"botToken\":\"{botToken}\",\"appToken\":\"{appToken}\"}}");

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor, env);

        Assert.Equal(1, exit);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("runtime_credential_mismatch", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("supply_runtime_credentials", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(appToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(appToken, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAgent_RejectedCredentials_Exit1WithErrorClassWithoutEcho()
    {
        const string botToken = "xoxb-wrong-bot-fixture-0123456789";
        const string appToken = "xapp-wrong-app-fixture-0123456789";
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            if (path == "/api/slack-manager/setup/progress?workspaceTeamId=T_W")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = EnrollmentProgress("ready"),
                }));

            if (path.EndsWith("/slack-manager/install-agent/credentials", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { accepted = false, runtimeCredentialValidationState = "failed", errorClass = "identity_mismatch" },
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    enrollmentId = "enrollment_1",
                    workspaceTeamId = "T_W",
                    connection = new { id = "connection_1", botName = "writer-bot" },
                    agentApp = new { id = "agent_app_1", runtimeCredentialValidationState = "not_provided" },
                    nextAction = "provide_credentials",
                    errorClass = (string?)null,
                },
            }));
        });
        fs.AddFile("/tmp/slack-credentials.json", $"{{\"appToken\":\"{appToken}\",\"botToken\":\"{botToken}\"}}");

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor, env);

        Assert.Equal(1, exit);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("identity_mismatch", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("rejected", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(appToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(appToken, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAgent_WorkspaceNotEnrolled_FailsWithSetupHintBeforeInstallPost()
    {
        var env = OperatorEnv();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            return Task.FromResult(RecordingHttpHandler.JsonError(
                "The workspace has not started setup.", "not_found", System.Net.HttpStatusCode.NotFound));
        });

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W"],
            output, error, fs, executor, env);

        Assert.Equal(1, exit);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request =>
            (request.RequestUri?.PathAndQuery ?? string.Empty).Contains("/install-agent", StringComparison.Ordinal));
        Assert.Contains("mo slack setup", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disable", "Disable a Slack Connection")]
    [InlineData("enable", "Enable a Slack Connection")]
    [InlineData("reconcile-create", "Reconcile an unknown managed Agent App create")]
    [InlineData("reconcile-delete", "Reconcile an unknown managed Agent App delete")]
    [InlineData("remove-binding", "Remove the Mohist Connection binding while retaining Agent App facts")]
    [InlineData("permanent-delete", "Permanently delete the managed Agent App after its Connection binding was removed")]
    public async Task SlackHelp_UsesLifecycleAndReconciliationTerminology(string command, string description)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["slack", command, "--help"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains(description, output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("setup", "installUrl")]
    [InlineData("status", "managedApps")]
    [InlineData("install-agent", "managedApp")]
    public async Task SlackJsonDiscovery_ListsFieldsWithoutRequiredInputsOrHttp(string command, string expectedField)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var args = new List<string> { "slack", command };
        if (command == "install-agent")
            args.Add("writer");
        args.Add("--json");
        var exit = await MohistCliCommands.RunAsync(http, args.ToArray(), output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains(expectedField, output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("required", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("reconcile-create", "/reconcile-create")]
    [InlineData("reconcile-delete", "/reconcile-delete")]
    [InlineData("remove-binding", "/remove-binding")]
    public async Task ManagedAppCommands_TargetManagerEndpoints(string command, string suffix)
    {
        await AssertEndpointAsync(
            [command, "connection_1"],
            $"/api/projects/proj_abc/slack-manager/connections/connection_1{suffix}",
            HttpMethod.Post);
    }

    [Fact]
    public async Task PermanentDelete_RequiresExplicitConfirmationAndTargetsManagerEndpoint()
    {
        await AssertEndpointAsync(
            ["permanent-delete", "connection_1", "--confirm", "DELETE"],
            "/api/projects/proj_abc/slack-manager/connections/connection_1/permanent-delete",
            HttpMethod.Post);
    }

    [Fact]
    public async Task PermanentDelete_Yes_ConfirmsAndTargetsManagerEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "permanent-delete", "connection_1", "--yes"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_abc/slack-manager/connections/connection_1/permanent-delete", request.RequestUri?.PathAndQuery);
        Assert.Equal("DELETE", JsonNode.Parse(request.Body!)!["confirmation"]!.GetValue<string>());
    }

    [Fact]
    public async Task PermanentDelete_WithoutYesOrConfirm_FailsBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "permanent-delete", "connection_1"], output, error, fs, executor);

        Assert.Equal(2, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("--yes", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearGap_TargetsConnectionEndpoint()
    {
        await AssertEndpointAsync(
            ["clear-gap", "connection_1"],
            "/api/projects/proj_abc/slack-connections/connection_1/clear-gap",
            HttpMethod.Post);
    }

    [Fact]
    public async Task MessageSend_PostsAgentReplyToTheReplyEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { accepted = true, connectionId = "connection_1", deliveryId = "slkout_1", dispatchRef = "slack-reply:connection_1:C1:1710000000.000100:terminal", merged = false },
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "message", "send", "--conversation", "C1", "--reply-to", "1710000000.000100", "--text", "All green. token=xoxb-leak"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_abc/slack-connections/reply", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("C1", body["conversationId"]!.GetValue<string>());
        Assert.Equal("1710000000.000100", body["threadTs"]!.GetValue<string>());
        // The CLI forwards the Agent-authored body verbatim; the Server redacts.
        Assert.Equal("All green. token=xoxb-leak", body["text"]!.GetValue<string>());
        Assert.Contains("accepted", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageSend_ReadsBodyFromStdinWhenTextIsDash()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { accepted = true, connectionId = "connection_1", deliveryId = "slkout_2", dispatchRef = "d", merged = false } })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "message", "send", "--conversation", "D1", "--text", "-"],
            output, error, fs, executor,
            standardInput: new StringReader("line one\nline two\n"));

        Assert.Equal(0, exit);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.Equal("line one\nline two\n", body["text"]!.GetValue<string>());
        Assert.Equal("D1", body["conversationId"]!.GetValue<string>());
        // --reply-to is optional (omitted for a DM).
        Assert.Null(body["threadTs"]?.GetValue<string?>());
    }

    [Fact]
    public async Task MessageSend_EmptyTextFailsBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "message", "send", "--conversation", "C1", "--text", "   "],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("--text is empty", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaimOwner_PrintsServerCodeOnce_WithAgentBotDmHint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { code = "claim_once", expiresAt = "2026-07-29T10:00:00Z", botName = "Mohist Agent" } })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "claim-owner", "connection_1"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(1, Count(output.ToString(), "claim_once"));
        Assert.Contains("Send the code to the Agent bot DM (Mohist Agent)", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/claim-owner", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ClaimOwner_WithoutBotName_FallsBackToGenericHint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { code = "claim_once", expiresAt = "2026-07-29T10:00:00Z" } })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "claim-owner", "connection_1"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains("Send the code to the Agent bot DM to claim ownership.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewListEdit_TargetConnectionEndpoints()
    {
        await AssertEndpointAsync(["view", "connection_1"], "/api/projects/proj_abc/slack-connections/connection_1/diagnostic", HttpMethod.Get);
        await AssertEndpointAsync(["list"], "/api/projects/proj_abc/slack-connections", HttpMethod.Get);
        await AssertEndpointAsync(["edit", "connection_1", "--bot-name", "helper"], "/api/projects/proj_abc/slack-connections/connection_1", HttpMethod.Patch);
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
            ["slack", "transfer-owner", "connection_1"], output, error, fs, executor);

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
            ["slack", command, "connection_1"], output, error, fs, executor);

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
            ["slack", "view", "connection_1"], output, error, fs, executor);

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
            ["slack", "list"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("primary state", text, StringComparison.Ordinal);
        Assert.Contains("disabled", text, StringComparison.Ordinal);
        Assert.Contains("credentials_invalid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic", handler.Requests.Select(r => r.RequestUri?.PathAndQuery).Single()!, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Edit_AccessPolicy_PostsToManageAccessWithPolicyAndMembers()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "edit", "connection_1", "--access-policy", "allowlist", "--allow-member", "U_A", "--allow-member", "U_B"],
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
            ["slack", "edit", "connection_1", "--access-policy", "allowlist", "--bot-name", "helper"],
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
            ["slack", "edit", "connection_1", "--access-policy", "owner_only", "--allow-member", "U_A"],
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
            ["slack", "edit", "connection_1", "--access-policy", "anyone", "--allow-member", "U_A", "--yes"],
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
            ["slack", "edit", "connection_1", "--access-policy", "bogus"],
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
            ["slack", "edit", "connection_1", "--access-policy", "anyone"],
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
            ["slack", "edit", "connection_1", "--access-policy", "anyone", "--yes"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/manage-access", request.RequestUri?.PathAndQuery);
        Assert.Equal("anyone", JsonNode.Parse(request.Body!)!["accessPolicy"]!.GetValue<string>());
    }

    private const string OperatorToken = "operator-token-for-slack-setup-test-0123456789";

    private static MockEnvironmentVariableProvider OperatorEnv(string token = OperatorToken)
    {
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env[OperatorCredentialProvider.TokenEnvironmentVariable] = token;
        return env;
    }

    private static object EnrollmentProgress(
        string nextAction,
        string? phase = null,
        string? installUrl = "https://api.slack.com/apps/A_1/oauth",
        string? errorClass = null) => new
    {
        enrollmentId = "enrollment_1",
        workspaceTeamId = "T_W",
        phase = phase ?? nextAction,
        managerAppId = "A_1",
        installUrl,
        nextAction,
        errorClass,
    };

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
            ["slack", "deliveries", "connection_1"], output, error, fs, executor);

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
            ["slack", "deliveries", "connection_1", "--only-uncertain"], output, error, fs, executor);

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
            ["slack", "resend-delivery", "connection_1", "slkout_uncertain_1"], output, error, fs, executor,
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
            ["slack", "resend-delivery", "connection_1", "slkout_pending_1"], output, error, fs, executor);

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
            ["slack", "resend-delivery", "connection_1", "slkout_uncertain_1"], output, error, fs, executor,
            terminalOverride: new CliTerminal(false));

        Assert.NotEqual(0, exit);
        Assert.Single(handler.Requests);
        Assert.Contains("--yes", error.ToString(), StringComparison.Ordinal);
    }

    private static async Task AssertEndpointAsync(string[] command, string path, HttpMethod method)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        var exit = await MohistCliCommands.RunAsync(http, ["slack", .. command], output, error, fs, executor);
        Assert.Equal(0, exit);
        Assert.Equal(method, handler.Requests.Single().Method);
        Assert.Equal(path, handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    private sealed class RecordingCliTerminal(string value) : ICliTerminal
    {
        public bool IsInputInteractive => true;
        public int HiddenReads { get; private set; }

        public Task<string?> ReadHiddenAsync(TextReader input, CancellationToken cancellationToken = default)
        {
            HiddenReads++;
            return Task.FromResult<string?>(value);
        }
    }
}
