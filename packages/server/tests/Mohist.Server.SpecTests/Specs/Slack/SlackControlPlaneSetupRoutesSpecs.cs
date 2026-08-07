using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("SlackControlPlaneRoutes")]
public sealed class SlackControlPlaneSetupRoutesSpecs
{
    private const string OperatorToken = MohistIntegrationFixture.OperatorToken;
    private readonly SlackControlPlaneRoutesFixture _fixture;

    public SlackControlPlaneSetupRoutesSpecs(SlackControlPlaneRoutesFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Supply_configuration_advances_to_awaiting_install_with_non_secret_progress_and_unique_next_action()
    {
        const string team = "T_CTRL_SETUP";
        _fixture.Configuration.Enqueue(RotationSucceeded(team, _fixture.TimeProvider.GetUtcNow().AddHours(12)));
        var createsBefore = _fixture.Apps.CreateCalls;

        using var client = _fixture.CreateOperatorClient();
        using var response = await client.PostAsJsonAsync("/api/slack-manager/setup/configuration", new
        {
            workspaceTeamId = team,
            configurationAccessToken = "xoxe-supplied",
            configurationRefreshToken = "xoxr-supplied",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var data = await ReadDataAsync(response);
        Assert.Equal("awaiting_install", data.GetProperty("phase").GetString());
        Assert.Equal("supply_runtime_credentials", data.GetProperty("nextAction").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("managerAppId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("installUrl").GetString()));
        Assert.Equal(createsBefore + 1, _fixture.Apps.CreateCalls);

        Assert.DoesNotContain("xoxe-supplied", body, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxr-supplied", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rerunning_configuration_restores_the_same_enrollment_and_app_without_a_second_create()
    {
        const string team = "T_CTRL_RERUN";
        _fixture.Configuration.Enqueue(RotationSucceeded(team, _fixture.TimeProvider.GetUtcNow().AddHours(12)));
        var createsBefore = _fixture.Apps.CreateCalls;

        using var client = _fixture.CreateOperatorClient();
        var first = await ReadDataAsync(await client.PostAsJsonAsync("/api/slack-manager/setup/configuration", new
        {
            workspaceTeamId = team,
            configurationAccessToken = "xoxe-a",
            configurationRefreshToken = "xoxr-a",
        }));
        var createsAfterFirst = _fixture.Apps.CreateCalls;

        _fixture.Configuration.Enqueue(RotationSucceeded(team, _fixture.TimeProvider.GetUtcNow().AddHours(12)));
        var second = await ReadDataAsync(await client.PostAsJsonAsync("/api/slack-manager/setup/configuration", new
        {
            workspaceTeamId = team,
            configurationAccessToken = "xoxe-b",
            configurationRefreshToken = "xoxr-b",
        }));

        Assert.Equal(first.GetProperty("enrollmentId").GetString(), second.GetProperty("enrollmentId").GetString());
        Assert.Equal(first.GetProperty("managerAppId").GetString(), second.GetProperty("managerAppId").GetString());
        Assert.Equal(createsBefore + 1, createsAfterFirst);
        Assert.Equal(createsAfterFirst, _fixture.Apps.CreateCalls);
    }

    [Fact]
    public async Task Supply_runtime_credentials_reports_socket_hello_next_action_without_binding_secret_address()
    {
        const string team = "T_CTRL_RUNTIME";
        _fixture.Configuration.Enqueue(RotationSucceeded(team, _fixture.TimeProvider.GetUtcNow().AddHours(12)));
        using var client = _fixture.CreateOperatorClient();
        var configuration = await ReadDataAsync(await client.PostAsJsonAsync("/api/slack-manager/setup/configuration", new
        {
            workspaceTeamId = team,
            configurationAccessToken = "xoxe",
            configurationRefreshToken = "xoxr",
        }));

        _fixture.BotIdentity.Result = new SlackBotIdentityVerificationResult(
            Verified: true,
            WorkspaceTeamId: team,
            BotUserId: "U_CTRL_BOT",
            AppId: configuration.GetProperty("managerAppId").GetString(),
            GrantedScopes: new HashSet<string> { "chat:write", "im:history", "users:read" });

        using var runtime = await client.PostAsJsonAsync("/api/slack-manager/setup/runtime-credentials", new
        {
            workspaceTeamId = team,
            botToken = "xoxb-runtime",
            appLevelToken = "xapp-candidate",
        });

        runtime.EnsureSuccessStatusCode();
        var data = await ReadDataAsync(runtime);
        Assert.Equal("awaiting_socket_validation", data.GetProperty("phase").GetString());
        Assert.Equal("report_socket_hello", data.GetProperty("nextAction").GetString());
        var body = await runtime.Content.ReadAsStringAsync();
        Assert.DoesNotContain("xoxb-runtime", body, StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-candidate", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/slack-manager/setup/configuration")]
    [InlineData("/api/slack-manager/setup/runtime-credentials")]
    public async Task Secret_setup_routes_require_an_operator_token_and_loopback(string path)
    {
        using var anonymous = _fixture.CreateUnauthenticatedClient();
        using var anonymousResponse = await anonymous.PostAsJsonAsync(path, SecretBody(path, "T_CTRL_AUTH"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(
            "Bearer error=\"invalid_token\"",
            Assert.Single(anonymousResponse.Headers.WwwAuthenticate).ToString());

        using var loopback = _fixture.CreateOperatorClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(SecretBody(path, "T_CTRL_AUTH")),
        };
        request.Headers.Add("X-Test-Remote-Address", "203.0.113.10");
        using var nonLoopback = await loopback.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, nonLoopback.StatusCode);
        Assert.Equal("loopback_required", await CodeAsync(nonLoopback));
    }

    [Fact]
    public async Task Setup_progress_requires_an_operator_token_and_reports_not_started_for_unknown_workspaces()
    {
        using var anonymous = _fixture.CreateUnauthenticatedClient();
        using var anonymousResponse = await anonymous.GetAsync("/api/slack-manager/setup/progress?workspaceTeamId=T_CTRL_PROGRESS");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(
            "Bearer error=\"invalid_token\"",
            Assert.Single(anonymousResponse.Headers.WwwAuthenticate).ToString());

        using var client = _fixture.CreateOperatorClient();
        using var unknown = await client.GetAsync("/api/slack-manager/setup/progress?workspaceTeamId=T_CTRL_UNKNOWN");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Caller_supplied_credential_address_in_secret_body_is_rejected()
    {
        using var client = _fixture.CreateOperatorClient();
        using var response = await client.PostAsJsonAsync("/api/slack-manager/setup/runtime-credentials", new
        {
            workspaceTeamId = "T_CTRL_ADDR",
            botToken = "xoxb",
            appLevelToken = "xapp",
            managerCredentialRef = "caller-must-not-supply-this",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("credential_address_not_supported", await CodeAsync(response));
    }

    [Fact]
    public async Task Legacy_setup_route_derives_the_credential_ref_from_owner_and_rejects_a_caller_supplied_one()
    {
        const string team = "T_LEGACY_SETUP";
        using var client = _fixture.CreateOperatorClient();

        using var rejected = await client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = "A_LEGACY",
            managerBotUserId = "U_LEGACY",
            managerCredentialRef = "caller-supplied-ref",
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("credential_address_not_supported", await CodeAsync(rejected));

        using var setup = await client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = "A_LEGACY",
            managerBotUserId = "U_LEGACY",
        });
        setup.EnsureSuccessStatusCode();
        var data = await ReadDataAsync(setup);
        Assert.True(data.GetProperty("enrollment").GetProperty("managerCredentialConfigured").GetBoolean());
    }

    private static object SecretBody(string path, string team) => path switch
    {
        "/api/slack-manager/setup/configuration" => new
        {
            workspaceTeamId = team,
            configurationAccessToken = "xoxe",
            configurationRefreshToken = "xoxr",
        },
        "/api/slack-manager/setup/runtime-credentials" => new
        {
            workspaceTeamId = team,
            botToken = "xoxb",
            appLevelToken = "xapp",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(path)),
    };

    private static SlackConfigurationCredentialRotationResult RotationSucceeded(string teamId, DateTimeOffset expiresAt) => new(
        SlackConfigurationCredentialRotationOutcome.Succeeded,
        new SlackConfigurationCredentialPair("xoxe-rotated", "xoxr-rotated"),
        teamId,
        expiresAt);

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }
}
