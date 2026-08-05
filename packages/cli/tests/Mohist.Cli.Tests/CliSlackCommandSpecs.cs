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
        foreach (var command in new[] { "setup", "status", "install-agent", "list", "view", "claim-owner", "edit", "transfer-owner", "enable", "disable", "remove-binding", "permanent-delete", "deliveries", "resend-delivery", "clear-gap", "reconcile-create", "reconcile-delete" })
            Assert.Contains(command, text, StringComparison.Ordinal);
        Assert.DoesNotContain("agent connection", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("setup", "--workspace-team", "--configuration-token-file", "--credentials-file", "--manager-app-id", "--manager-bot-user-id", "--manager-credential-ref")]
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
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[]
                {
                    new
                    {
                        agentId = "agent_1",
                        agentName = "writer",
                        connection = new { id = "connection_1", botName = "writer-bot" },
                        managedApp = new { id = "child_app_1", nextAction = "ready" },
                    },
                },
            }));
        });

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W", "--json", "connection,nextAction"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        var projected = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal("connection_1", projected["connection"]!["id"]!.GetValue<string>());
        Assert.Equal("ready", projected["nextAction"]!.GetValue<string>());
        Assert.Equal(2, projected.Count);
    }

    [Fact]
    public async Task Setup_EnrollsProvisionsAndPrintsClaimCodeWithoutEchoingCredential()
    {
        const string operatorToken = "operator-token-for-slack-setup-test-0123456789";
        const string managerCredential = "manager-credential-fixture";
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env[OperatorCredentialProvider.TokenEnvironmentVariable] = operatorToken;
        var statusCalls = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (request.RequestUri?.PathAndQuery == "/api/slack-manager/status?workspaceTeamId=T_W")
            {
                statusCalls++;
                return Task.FromResult(statusCalls == 1
                    ? RecordingHttpHandler.JsonError("The workspace has not been enrolled.", "not_found", System.Net.HttpStatusCode.NotFound)
                    : statusCalls == 2
                        ? RecordingHttpHandler.Json(new
                        {
                            success = true,
                            data = new
                            {
                                enrollment = new { id = "enrollment_1", workspaceTeamId = "T_W" },
                                connections = Array.Empty<object>(),
                                managedApps = Array.Empty<object>(),
                                nextAction = "configure_manager_credentials",
                            },
                        })
                        : RecordingHttpHandler.Json(new
                        {
                            success = true,
                            data = new
                            {
                                enrollment = new { id = "enrollment_1", workspaceTeamId = "T_W" },
                                connections = Array.Empty<object>(),
                                managedApps = Array.Empty<object>(),
                                nextAction = "claim_manager",
                            },
                        }));
            }

            if (request.RequestUri?.PathAndQuery == "/api/slack-manager/setup")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        enrollment = new { id = "enrollment_1", workspaceTeamId = "T_W" },
                        claimCode = "claim_once",
                        claimExpiresAt = "2026-08-10T00:00:00Z",
                        nextAction = "configure_manager_credentials",
                    },
                }));

            if (request.RequestUri?.PathAndQuery == "/api/slack-manager/credentials")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { workspaceTeamId = "T_W", credentialProvisioned = true },
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        });
        fs.AddFile("/tmp/manager-credentials.json", $"{{\"botToken\":\"{managerCredential}\"}}");

        var exit = await MohistCliCommands.RunAsync(
            http,
            [
                "slack", "setup",
                "--workspace-team", "T_W",
                "--manager-app-id", "A_1",
                "--manager-bot-user-id", "U_1",
                "--manager-credential-ref", "ref_1",
                "--credentials-file", "/tmp/manager-credentials.json",
            ],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/slack-manager/status?workspaceTeamId=T_W", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal(operatorToken, Assert.Single(handler.Requests[0].Headers[OperatorCredentialProvider.HeaderName]));

        var setupRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, setupRequest.Method);
        Assert.Equal("/api/slack-manager/setup", setupRequest.RequestUri?.PathAndQuery);
        var setupBody = JsonNode.Parse(setupRequest.Body!)!;
        Assert.Equal("T_W", setupBody["workspaceTeamId"]!.GetValue<string>());
        Assert.Equal("A_1", setupBody["managerAppId"]!.GetValue<string>());
        Assert.Equal("U_1", setupBody["managerBotUserId"]!.GetValue<string>());
        Assert.Equal("ref_1", setupBody["managerCredentialRef"]!.GetValue<string>());
        Assert.Equal("socket", setupBody["transportKind"]!.GetValue<string>());
        Assert.Equal("ready", setupBody["readiness"]!.GetValue<string>());

        var credentialRequest = handler.Requests[3];
        Assert.Equal(HttpMethod.Post, credentialRequest.Method);
        Assert.Equal("/api/slack-manager/credentials", credentialRequest.RequestUri?.PathAndQuery);
        Assert.Equal(managerCredential, JsonNode.Parse(credentialRequest.Body!)!["managerBotToken"]!.GetValue<string>());

        var stdout = output.ToString();
        Assert.Equal(1, Count(stdout, "claim_once"));
        Assert.Contains("valid until", stdout, StringComparison.Ordinal);
        Assert.Contains("Mohist App bot", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(managerCredential, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(managerCredential, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Workspace T_W is enrolled.", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Manager credential provisioned for T_W.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_ResumesFromProvisionedStateWithoutReEnrolling()
    {
        const string operatorToken = "operator-token-for-slack-setup-test-0123456789";
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env[OperatorCredentialProvider.TokenEnvironmentVariable] = operatorToken;
        var statusCalls = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (string.Equals(request.RequestUri?.PathAndQuery, "/api/slack-manager/status?workspaceTeamId=T_W", StringComparison.Ordinal))
            {
                statusCalls++;
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        enrollment = new { id = "enrollment_1" },
                        connections = Array.Empty<object>(),
                        managedApps = Array.Empty<object>(),
                        nextAction = statusCalls == 1 ? "configure_manager_credentials" : "ready",
                    },
                }));
            }

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { workspaceTeamId = "T_W", credentialProvisioned = true },
            }));
        });
        fs.AddFile("/tmp/manager-credentials.json", "{\"botToken\":\"manager-credential-fixture\"}");

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W", "--credentials-file", "/tmp/manager-credentials.json"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        Assert.DoesNotContain(handler.Requests, request => string.Equals(request.RequestUri?.PathAndQuery, "/api/slack-manager/setup", StringComparison.Ordinal));
        Assert.Contains("Manager credential provisioned for T_W.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_ReadyWorkspacePrintsSummaryWithoutMutation()
    {
        const string operatorToken = "operator-token-for-slack-setup-test-0123456789";
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env[OperatorCredentialProvider.TokenEnvironmentVariable] = operatorToken;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    enrollment = new { id = "enrollment_1" },
                    connections = Array.Empty<object>(),
                    managedApps = Array.Empty<object>(),
                    nextAction = "ready",
                },
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
    public async Task Setup_ReadyWithCredentialsFile_RotatesCredentialOnce()
    {
        const string operatorToken = "operator-token-for-slack-setup-test-0123456789";
        const string rotatedCredential = "rotated-manager-credential-fixture";
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env[OperatorCredentialProvider.TokenEnvironmentVariable] = operatorToken;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
            request.RequestUri?.PathAndQuery == "/api/slack-manager/credentials"
                ? Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { workspaceTeamId = "T_W", credentialProvisioned = true },
                }))
                : Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        enrollment = new { id = "enrollment_1" },
                        connections = Array.Empty<object>(),
                        managedApps = Array.Empty<object>(),
                        nextAction = "ready",
                    },
                })));
        fs.AddFile("/tmp/manager-credentials.json", $"{{\"botToken\":\"{rotatedCredential}\"}}");

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W", "--credentials-file", "/tmp/manager-credentials.json"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        var credentialPosts = handler.Requests
            .Where(request => request.RequestUri?.PathAndQuery == "/api/slack-manager/credentials")
            .ToList();
        Assert.Single(credentialPosts);
        Assert.Equal(rotatedCredential, JsonNode.Parse(credentialPosts[0].Body!)!["managerBotToken"]!.GetValue<string>());
        Assert.Contains("Manager credential rotated for T_W.", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedCredential, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedCredential, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_ClaimPhaseRerun_ReissuesCodeWithManagerFacts()
    {
        const string operatorToken = "operator-token-for-slack-setup-test-0123456789";
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env[OperatorCredentialProvider.TokenEnvironmentVariable] = operatorToken;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
            request.RequestUri?.PathAndQuery == "/api/slack-manager/status?workspaceTeamId=T_W"
                ? Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        enrollment = new { id = "enrollment_1" },
                        connections = Array.Empty<object>(),
                        managedApps = Array.Empty<object>(),
                        nextAction = "claim_manager",
                    },
                }))
                : Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        enrollment = new { id = "enrollment_1" },
                        claimCode = "claim_fresh",
                        claimExpiresAt = "2026-08-11T00:00:00Z",
                        nextAction = "claim_manager",
                    },
                })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            [
                "slack", "setup",
                "--workspace-team", "T_W",
                "--manager-app-id", "A_1",
                "--manager-bot-user-id", "U_1",
                "--manager-credential-ref", "ref_1",
            ],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        var setupPosts = handler.Requests
            .Where(request => request.RequestUri?.PathAndQuery == "/api/slack-manager/setup")
            .ToList();
        Assert.Single(setupPosts);
        Assert.Contains("claim_fresh", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_MissingManagerFactsAtEnrollmentStep_FailsWithUsageBeforeHttpMutation()
    {
        const string operatorToken = "operator-token-for-slack-setup-test-0123456789";
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env[OperatorCredentialProvider.TokenEnvironmentVariable] = operatorToken;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "The workspace has not been enrolled.", "not_found", System.Net.HttpStatusCode.NotFound)));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W"],
            output, error, fs, executor, env);

        Assert.Equal(2, exit);
        Assert.DoesNotContain(handler.Requests, request => string.Equals(request.RequestUri?.PathAndQuery, "/api/slack-manager/setup", StringComparison.Ordinal));
        var stderr = error.ToString();
        Assert.Contains("--manager-app-id", stderr, StringComparison.Ordinal);
        Assert.Contains("--manager-bot-user-id", stderr, StringComparison.Ordinal);
        Assert.Contains("--manager-credential-ref", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_ConfigurationTokenFile_ShapeValidatedBeforeHttpWithoutEcho()
    {
        const string configurationToken = "configuration-token-fixture";
        const string operatorToken = "operator-token-for-slack-setup-test-0123456789";
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env[OperatorCredentialProvider.TokenEnvironmentVariable] = operatorToken;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    enrollment = new { id = "enrollment_1" },
                    connections = Array.Empty<object>(),
                    managedApps = Array.Empty<object>(),
                    nextAction = "ready",
                },
            })));
        fs.AddFile("/tmp/configuration-token.json", $"{{\"configurationToken\":\"{configurationToken}\",\"configurationRefreshToken\":\"refresh-fixture\"}}");

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W", "--configuration-token-file", "/tmp/configuration-token.json"],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        Assert.Single(handler.Requests);
        Assert.Contains("Configuration token pair accepted", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(configurationToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(configurationToken, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Setup_ConfigurationTokenFile_RejectsSymlinkOrWorldReadableFileBeforeHttp(bool symlink, bool worldReadable)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        fs.AddFile("/tmp/configuration-token.json", "{\"configurationToken\":\"ct\",\"configurationRefreshToken\":\"cr\"}");
        fs.TreatFilesAsSymbolicLinks = symlink;
        fs.TreatFilesAsWorldReadable = worldReadable;

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "setup", "--workspace-team", "T_W", "--configuration-token-file", "/tmp/configuration-token.json"],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("ct", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ct", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_SelectedJson_ProjectsRequestedFieldsAfterWizard()
    {
        const string operatorToken = "operator-token-for-slack-setup-test-0123456789";
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env[OperatorCredentialProvider.TokenEnvironmentVariable] = operatorToken;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
            request.RequestUri?.PathAndQuery == "/api/slack-manager/status?workspaceTeamId=T_W"
                ? Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        enrollment = new { id = "enrollment_1" },
                        connections = Array.Empty<object>(),
                        managedApps = Array.Empty<object>(),
                        nextAction = "claim_manager",
                    },
                }))
                : Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        enrollment = new { id = "enrollment_1" },
                        claimCode = "claim_json",
                        claimExpiresAt = "2026-08-12T00:00:00Z",
                        nextAction = "claim_manager",
                    },
                })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            [
                "slack", "setup",
                "--workspace-team", "T_W",
                "--manager-app-id", "A_1",
                "--manager-bot-user-id", "U_1",
                "--manager-credential-ref", "ref_1",
                "--json", "claimCode,nextAction",
            ],
            output, error, fs, executor, env);

        Assert.Equal(0, exit);
        var projected = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal("claim_json", projected["claimCode"]!.GetValue<string>());
        Assert.Equal("claim_manager", projected["nextAction"]!.GetValue<string>());
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
    public async Task InstallAgent_CreatesAppDrivesPhasesAndStopsAtInstallAuthorization()
    {
        var optionsCall = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            if (path.EndsWith("/slack-manager/agents?workspaceTeamId=T_W", StringComparison.Ordinal))
            {
                optionsCall++;
                return Task.FromResult(optionsCall == 1
                    ? RecordingHttpHandler.Json(new
                    {
                        success = true,
                        data = new object[]
                        {
                            new { agentId = "agent_1", agentName = "writer", connection = (object?)null, managedApp = (object?)null },
                        },
                    })
                    : RecordingHttpHandler.Json(new
                    {
                        success = true,
                        data = new object[]
                        {
                            new
                            {
                                agentId = "agent_1",
                                agentName = "writer",
                                connection = new { id = "connection_1", botName = "writer" },
                                managedApp = new { id = "child_app_1", nextAction = "authorize_child_app" },
                            },
                        },
                    }));
            }

            if (path.EndsWith("/slack-manager/apps", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        created = true,
                        connection = new { id = "connection_1", botName = "writer" },
                        managedApp = new { id = "child_app_1", nextAction = "create_child_app" },
                    },
                }));

            if (path.EndsWith("/connections/connection_1/create", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "child_app_1" },
                }));

            if (path.EndsWith("/connections/connection_1/begin-authorization", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { state = "oauth_state_1", expiresAt = "2026-08-10T00:00:00Z", authorizationAttemptId = "attempt_1" },
                }));

            if (path.EndsWith("/connections/connection_1/authorization-progress", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { authorization = "awaiting_user" },
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        });

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(
            [
                "/api/projects/proj_abc/agents?all=true",
                "/api/projects/proj_abc/slack-manager/agents?workspaceTeamId=T_W",
                "/api/projects/proj_abc/slack-manager/apps",
                "/api/projects/proj_abc/slack-manager/connections/connection_1/create",
                "/api/projects/proj_abc/slack-manager/agents?workspaceTeamId=T_W",
                "/api/projects/proj_abc/slack-manager/connections/connection_1/begin-authorization",
                "/api/projects/proj_abc/slack-manager/connections/connection_1/authorization-progress",
            ],
            handler.Requests.Select(request => request.RequestUri?.PathAndQuery ?? string.Empty).ToArray());

        var appsBody = JsonNode.Parse(handler.Requests[2].Body!)!;
        Assert.Equal("agent_1", appsBody["agentId"]!.GetValue<string>());
        Assert.Equal("T_W", appsBody["workspaceTeamId"]!.GetValue<string>());
        Assert.Equal("owner_only", appsBody["accessPolicy"]!.GetValue<string>());

        var progressBody = JsonNode.Parse(handler.Requests[6].Body!)!;
        Assert.Equal("awaiting_user", progressBody["authorization"]!.GetValue<string>());

        var stdout = output.ToString();
        Assert.Contains("Installation authorization is required", stdout, StringComparison.Ordinal);
        Assert.Contains("oauth_state_1", stdout, StringComparison.Ordinal);
        Assert.Contains("mo slack install-agent <agent>", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("token", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAgent_ResumesExistingConnectionWithoutCreatingSecondApp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[]
                {
                    new
                    {
                        agentId = "agent_1",
                        agentName = "writer",
                        connection = new { id = "connection_1", botName = "writer" },
                        managedApp = new { id = "child_app_1", nextAction = "wait_for_operation" },
                    },
                },
            }));
        });

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => (request.RequestUri?.PathAndQuery ?? string.Empty).EndsWith("/slack-manager/apps", StringComparison.Ordinal));
        Assert.Contains("in progress", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("wait_for_operation", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAgent_Ready_PrintsConnectionAndBotSummary()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[]
                {
                    new
                    {
                        agentId = "agent_1",
                        agentName = "writer",
                        connection = new { id = "connection_1", botName = "writer-bot" },
                        managedApp = new { id = "child_app_1", nextAction = "ready" },
                    },
                },
            }));
        });

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains("installed and ready in workspace T_W", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("connection_1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("writer-bot", output.ToString(), StringComparison.Ordinal);
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
    public async Task InstallAgent_CredentialsFile_ValidatedAtAuthorizationStepWithoutEcho()
    {
        const string appToken = "xapp-secret-fixture";
        const string botToken = "xoxb-secret-fixture";
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            if (path.EndsWith("/slack-manager/agents?workspaceTeamId=T_W", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[]
                    {
                        new
                        {
                            agentId = "agent_1",
                            agentName = "writer",
                            connection = new { id = "connection_1", botName = "writer" },
                            managedApp = new { id = "child_app_1", nextAction = "authorize_child_app" },
                        },
                    },
                }));

            if (path.EndsWith("/begin-authorization", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { state = "oauth_state_1", expiresAt = "2026-08-10T00:00:00Z", authorizationAttemptId = "attempt_1" },
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { authorization = "awaiting_user" },
            }));
        });
        fs.AddFile("/tmp/slack-credentials.json", $"{{\"appToken\":\"{appToken}\",\"botToken\":\"{botToken}\"}}");

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.DoesNotContain(appToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(appToken, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InstallAgent_CredentialsFile_RejectsSymlinkOrWorldReadableFileAtAuthorizationStep(bool symlink, bool worldReadable)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new object[] { new { id = "agent_1", name = "writer" } },
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[]
                {
                    new
                    {
                        agentId = "agent_1",
                        agentName = "writer",
                        connection = new { id = "connection_1", botName = "writer" },
                        managedApp = new { id = "child_app_1", nextAction = "authorize_child_app" },
                    },
                },
            }));
        });
        fs.AddFile("/tmp/slack-credentials.json", "{\"appToken\":\"xapp-secret\",\"botToken\":\"xoxb-secret\"}");
        fs.TreatFilesAsSymbolicLinks = symlink;
        fs.TreatFilesAsWorldReadable = worldReadable;

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "install-agent", "writer", "--workspace-team", "T_W", "--credentials-file", "/tmp/slack-credentials.json"],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("xapp-secret", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-secret", error.ToString(), StringComparison.Ordinal);
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
    [InlineData("setup", "claimCode")]
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
    public async Task ClearGap_TargetsConnectionEndpoint()
    {
        await AssertEndpointAsync(
            ["clear-gap", "connection_1"],
            "/api/projects/proj_abc/slack-connections/connection_1/clear-gap",
            HttpMethod.Post);
    }

    [Fact]
    public async Task ClaimOwner_PrintsServerCodeOnce()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { code = "claim_once", expiresAt = "2026-07-29T10:00:00Z" } })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "claim-owner", "connection_1"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(1, Count(output.ToString(), "claim_once"));
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/claim-owner", handler.Requests.Single().RequestUri?.PathAndQuery);
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
