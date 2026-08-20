using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliManagerCapabilitySpecs
{
    private const string OperatorToken = "operator-token-for-manager-capability-test-0123456789";

    [Theory]
    [InlineData("slack", "setup")]
    [InlineData("slack", "remove-binding", "connection_1")]
    [InlineData("slack", "permanent-delete", "connection_1", "--yes")]
    [InlineData("slack", "deliveries", "connection_1")]
    [InlineData("slack", "resend-delivery", "connection_1", "delivery_1", "--yes")]
    [InlineData("slack", "message", "send", "--conversation", "D_1", "--text", "secret")]
    [InlineData("agent", "archive", "agent_1")]
    [InlineData("otel", "query", "SELECT 1")]
    public async Task Manager_mode_rejects_unlisted_commands_before_http(params string[] command)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        var env = ManagerEnv();

        var exit = await MohistCliCommands.RunAsync(
            http, ["--manager", .. command], output, error, fs, executor, env);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("unavailable in Manager mode", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manager_mode_executes_status_through_existing_route_and_marks_request()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { nextAction = "configure_manager_credentials", enrollment = new { id = "enrollment_1" } },
            })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["--manager", "slack", "status", "--workspace-team", "T_1"],
            output, error, fs, executor, OperatorEnv());

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/slack-manager/status?workspaceTeamId=T_1", request.RequestUri?.PathAndQuery);
        Assert.Equal("1", Assert.Single(request.Headers[ManagerCapabilityCatalog.ManagerModeHeader]));
        Assert.Contains("configure_manager_credentials", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manager_mode_access_policy_uses_existing_service_projection()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    connection = new { id = "connection_1", accessPolicy = "allowlist" },
                    accessPolicy = "allowlist",
                    allowMembers = new[] { "U_1" },
                    nextAction = "none",
                },
            })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "edit", "connection_1", "--access-policy", "allowlist", "--allow-member", "U_1"],
            output, error, fs, executor, ManagerEnv());

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/manage-access", request.RequestUri?.PathAndQuery);
        Assert.Equal("allowlist", JsonNode.Parse(request.Body!)!["accessPolicy"]!.GetValue<string>());
        Assert.Contains("nextAction", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("1", Assert.Single(request.Headers[ManagerCapabilityCatalog.ManagerModeHeader]));
    }

    [Fact]
    public async Task Manager_mode_diagnostics_returns_authoritative_connection_projection()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    primaryState = "agent_needs_setup",
                    nextAction = "configure_agent_runtime",
                    facts = new { agentReadiness = "needs_setup" },
                },
            })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "diagnostics", "connection_1"],
            output, error, fs, executor, ManagerEnv());

        Assert.Equal(0, exit);
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/diagnostic", Assert.Single(handler.Requests).RequestUri?.PathAndQuery);
        Assert.Contains("configure_agent_runtime", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manager_mode_create_mounts_existing_agent_and_returns_service_result()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (request.RequestUri?.PathAndQuery == "/api/projects/proj_abc/agents?all=true")
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_1", name = "writer" } },
                }));

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    created = true,
                    connection = new { id = "connection_1", desiredState = "enabled" },
                    managedApp = new { id = "app_1", nextAction = "create_agent_app" },
                },
            }));
        });

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "create", "writer", "--workspace-team", "T_1"],
            output, error, fs, executor, ManagerEnv());

        Assert.Equal(0, exit);
        Assert.Equal(
            new string?[]
            {
                "/api/projects/proj_abc/agents?all=true",
                "/api/projects/proj_abc/slack-manager/apps",
            },
            handler.Requests.Select(request => request.RequestUri?.PathAndQuery).ToArray());
        Assert.Contains("create_agent_app", output.ToString(), StringComparison.Ordinal);
        Assert.All(handler.Requests, request =>
            Assert.Equal("1", Assert.Single(request.Headers[ManagerCapabilityCatalog.ManagerModeHeader])));
    }

    [Fact]
    public async Task Manager_mode_owner_transfer_does_not_print_one_time_code()
    {
        const string protectedCode = "claim-code-must-not-reach-manager-agent";
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { code = protectedCode, expiresAt = "2026-08-20T18:00:00Z", botName = "agent-bot" },
            })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "transfer-owner", "connection_1"],
            output, error, fs, executor, ManagerEnv());

        Assert.Equal(0, exit);
        Assert.DoesNotContain(protectedCode, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(protectedCode, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("expiresAt", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("code", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Existing_operator_mode_keeps_unmarked_cli_behavior()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { id = "connection_1" },
            })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["slack", "enable", "connection_1"],
            output, error, fs, executor, OperatorEnv());

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.DoesNotContain(ManagerCapabilityCatalog.ManagerModeHeader, request.Headers.Select(header => header.Key));
        Assert.Equal("/api/projects/proj_abc/slack-connections/connection_1/enable", request.RequestUri?.PathAndQuery);
    }

    private static MockEnvironmentVariableProvider ManagerEnv()
    {
        var env = OperatorEnv();
        env[ManagerCapabilityCatalog.ManagerModeEnvironmentVariable] = "1";
        return env;
    }

    private static MockEnvironmentVariableProvider OperatorEnv()
    {
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["MOHIST_TOKEN"] = OperatorToken;
        return env;
    }
}
