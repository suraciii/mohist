using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Slack.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Collection("SlackControlPlaneRoutes")]
[Trait("level", "L1")]
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
            configurationAccessToken = "xoxe-supplied",
            configurationRefreshToken = "xoxr-supplied",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var data = await ReadDataAsync(response);
        Assert.Equal("awaiting_install", data.GetProperty("phase").GetString());
        Assert.Equal("approve_install", data.GetProperty("primaryAction").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("installUrl").GetString()));
        Assert.Contains(team, data.GetProperty("summary").GetString(), StringComparison.Ordinal);
        // The projection carries one primary action and its supporting facts;
        // internal setup steps never compete as a second task.
        string[] projectedFields = ["phase", "primaryAction", "installUrl", "summary", "errorClass"];
        Assert.All(data.EnumerateObject(), property =>
            Assert.Contains(property.Name, projectedFields));
        Assert.False(data.TryGetProperty("managerAppId", out _));
        Assert.False(data.TryGetProperty("enrollmentId", out _));
        Assert.False(data.TryGetProperty("workspaceTeamId", out _));
        Assert.Equal(createsBefore + 1, _fixture.Apps.CreateCalls);

        Assert.DoesNotContain("xoxe-supplied", body, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxr-supplied", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_progress_selects_the_named_workspace_and_reports_the_ambiguity_without_one()
    {
        const string firstTeam = "T_CTRL_SEL_A";
        const string secondTeam = "T_CTRL_SEL_B";
        using var client = _fixture.CreateOperatorClient();
        await EnrollAsync(client, firstTeam);
        await EnrollAsync(client, secondTeam);

        using var selected = await client.GetAsync(
            $"/api/slack-manager/setup/progress?workspaceTeamId={secondTeam}");
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var selectedData = await ReadDataAsync(selected);
        Assert.Equal("awaiting_install", selectedData.GetProperty("phase").GetString());
        Assert.Equal("approve_install", selectedData.GetProperty("primaryAction").GetString());
        Assert.Contains(secondTeam, selectedData.GetProperty("summary").GetString(), StringComparison.Ordinal);

        using var ambiguous = await client.GetAsync("/api/slack-manager/setup/progress");
        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);
        Assert.Equal("workspace_selection_required", await CodeAsync(ambiguous));
        var choices = await ReadDetailsAsync(ambiguous);
        Assert.True(choices.GetArrayLength() >= 2);
        var offered = choices.EnumerateArray()
            .Select(choice => choice.GetProperty("teamId").GetString())
            .ToArray();
        Assert.Contains(firstTeam, offered);
        Assert.Contains(secondTeam, offered);
        Assert.All(choices.EnumerateArray(), choice =>
        {
            Assert.False(string.IsNullOrWhiteSpace(choice.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(choice.GetProperty("phase").GetString()));
        });

        using var unknown = await client.GetAsync("/api/slack-manager/setup/progress?workspaceTeamId=T_CTRL_SEL_MISSING");
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        Assert.Equal("workspace_not_enrolled", await CodeAsync(unknown));
    }

    [Fact]
    public async Task Resume_advances_the_selected_workspace_and_refuses_to_pick_the_first_one()
    {
        const string firstTeam = "T_CTRL_RESUME_A";
        const string secondTeam = "T_CTRL_RESUME_B";
        using var client = _fixture.CreateOperatorClient();
        await EnrollAsync(client, firstTeam);
        await EnrollAsync(client, secondTeam);
        var createsBefore = _fixture.Apps.CreateCalls;

        using var ambiguous = await client.PostAsync("/api/slack-manager/setup/resume", content: null);
        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);
        Assert.Equal("workspace_selection_required", await CodeAsync(ambiguous));
        Assert.Equal(createsBefore, _fixture.Apps.CreateCalls);

        using var selected = await client.PostAsync(
            $"/api/slack-manager/setup/resume?workspaceTeamId={secondTeam}", content: null);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var data = await ReadDataAsync(selected);
        Assert.Equal("awaiting_install", data.GetProperty("phase").GetString());
        Assert.Contains(secondTeam, data.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal(createsBefore, _fixture.Apps.CreateCalls);
    }

    [Fact]
    public async Task Runtime_credentials_with_an_unknown_workspace_selector_fail_before_any_verification()
    {
        using var client = _fixture.CreateOperatorClient();
        var verificationsBefore = _fixture.BotIdentity.Requests.Count;

        using var response = await client.PostAsJsonAsync(
            "/api/slack-manager/setup/runtime-credentials?workspaceTeamId=T_CTRL_RUNTIME_MISSING",
            new { botToken = "xoxb", appLevelToken = "xapp" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("workspace_not_enrolled", await CodeAsync(response));
        Assert.Equal(verificationsBefore, _fixture.BotIdentity.Requests.Count);
    }

    [Fact]
    public async Task Runtime_credentials_bind_to_the_selected_workspace_and_a_mismatch_changes_no_target()
    {
        const string selectedTeam = "T_CTRL_RUNTIME_A";
        const string otherTeam = "T_CTRL_RUNTIME_B";
        using var client = _fixture.CreateOperatorClient();
        await EnrollAsync(client, selectedTeam);
        await EnrollAsync(client, otherTeam);

        // The pair verifies as another Workspace's Bot: the selected target
        // keeps its credentials and no other target is written.
        _fixture.BotIdentity.Result = VerifiedBot(otherTeam, "A_OTHER_APP");
        using var response = await client.PostAsJsonAsync(
            $"/api/slack-manager/setup/runtime-credentials?workspaceTeamId={selectedTeam}",
            new { botToken = "xoxb-foreign", appLevelToken = "xapp-foreign" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("failed", data.GetProperty("phase").GetString());
        Assert.Equal("supply_runtime_credentials", data.GetProperty("primaryAction").GetString());
        Assert.Equal("runtime_credential_mismatch", data.GetProperty("errorClass").GetString());
        Assert.Contains(selectedTeam, data.GetProperty("summary").GetString(), StringComparison.Ordinal);

        var runtimeStates = await ReadRuntimeStatesAsync();
        Assert.All(runtimeStates, state => Assert.Equal("not_provided", state));
        Assert.DoesNotContain("xoxb-foreign", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_setup_adjudicate_create_route_is_retired()
    {
        const string team = "T_CTRL_ADJUDICATE_RETIRED";
        using var client = _fixture.CreateOperatorClient();
        using var response = await client.PostAsync(
            $"/api/slack-manager/setup/adjudicate-create?workspaceTeamId={team}", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
    public async Task Legacy_setup_route_is_removed()
    {
        using var client = _fixture.CreateOperatorClient();

        using var response = await client.PostAsJsonAsync("/api/slack-manager/setup", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Resume_requires_an_operator_token_and_loopback()
    {
        using var anonymous = _fixture.CreateUnauthenticatedClient();
        using var anonymousResponse = await anonymous.PostAsync(
            "/api/slack-manager/setup/resume",
            content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var loopback = _fixture.CreateOperatorClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/slack-manager/setup/resume");
        request.Headers.Add("X-Test-Remote-Address", "203.0.113.10");
        using var nonLoopback = await loopback.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, nonLoopback.StatusCode);
        Assert.Equal("loopback_required", await CodeAsync(nonLoopback));
    }

    private static object SecretBody(string path, string team) => path switch
    {
        "/api/slack-manager/setup/configuration" => new
        {
            configurationAccessToken = "xoxe",
            configurationRefreshToken = "xoxr",
        },
        "/api/slack-manager/setup/runtime-credentials" => new
        {
            botToken = "xoxb",
            appLevelToken = "xapp",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(path)),
    };

    private async Task EnrollAsync(HttpClient client, string team)
    {
        _fixture.Configuration.Enqueue(
            RotationSucceeded(team, _fixture.TimeProvider.GetUtcNow().AddHours(12)));
        using var response = await client.PostAsJsonAsync("/api/slack-manager/setup/configuration", new
        {
            configurationAccessToken = "xoxe-supplied",
            configurationRefreshToken = "xoxr-supplied",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Contains(team, data.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    private async Task<List<string>> ReadRuntimeStatesAsync()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return await db.SlackWorkspaceEnrollments.AsNoTracking()
            .Where(row => row.RuntimeCredentialValidationState != "not_provided")
            .Select(row => row.RuntimeCredentialValidationState)
            .ToListAsync();
    }

    private static SlackBotIdentityVerificationResult VerifiedBot(string teamId, string appId) => new(
        Verified: true,
        WorkspaceTeamId: teamId,
        BotUserId: "U_CTRL_BOT",
        AppId: appId,
        GrantedScopes: new HashSet<string> { "chat:write", "im:history", "users:read" });

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

    private static async Task<JsonElement> ReadDetailsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("details").Clone();
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }
}
