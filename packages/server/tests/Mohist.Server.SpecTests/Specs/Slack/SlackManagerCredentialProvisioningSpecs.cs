using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Security;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackManagerCredentialProvisioningSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagerCredentialProvisioningSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Manager_credential_provisioning_is_operator_only_reference_derived_rotatable_and_nonsecret()
    {
        const string team = "T_MANAGER_CREDENTIALS";
        const string credentialRef = "manager-credential-reference";
        const string firstCredential = "manager-credential-first";
        const string rotatedCredential = "manager-credential-rotated";

        using var setup = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = "A_MANAGER_CREDENTIALS",
            managerBotUserId = "U_MANAGER_CREDENTIALS",
            managerCredentialRef = credentialRef,
        });
        setup.EnsureSuccessStatusCode();
        var setupData = await ReadDataAsync(setup);
        Assert.True(setupData.GetProperty("enrollment").GetProperty("managerCredentialConfigured").GetBoolean());
        Assert.False(setupData.GetProperty("enrollment").GetProperty("managerCredentialProvisioned").GetBoolean());

        using var unauthenticated = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/slack-manager/credentials")
        {
            Content = JsonContent.Create(new { workspaceTeamId = team, managerBotToken = firstCredential }),
        };
        _fixture.Client.DefaultRequestHeaders.Remove(OperatorCredential.HeaderName);
        HttpResponseMessage unauthorizedResponse;
        try
        {
            unauthorizedResponse = await _fixture.Client.SendAsync(unauthenticated);
        }
        finally
        {
            _fixture.Client.DefaultRequestHeaders.Add(
                OperatorCredential.HeaderName,
                MohistIntegrationFixture.OperatorToken);
        }
        using (unauthorizedResponse)
        {
            Assert.Equal(HttpStatusCode.Forbidden, unauthorizedResponse.StatusCode);
            using var error = JsonDocument.Parse(await unauthorizedResponse.Content.ReadAsStringAsync());
            Assert.Equal("operator_credential_required", error.RootElement.GetProperty("code").GetString());
        }

        using var first = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/credentials", new
        {
            workspaceTeamId = team,
            managerBotToken = firstCredential,
            managerCredentialRef = "caller-supplied-reference-must-not-be-used",
        });
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        using (var addressError = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
            Assert.Equal("credential_address_not_supported", addressError.RootElement.GetProperty("code").GetString());

        using var provisioned = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/credentials", new
        {
            workspaceTeamId = team,
            managerBotToken = firstCredential,
        });
        provisioned.EnsureSuccessStatusCode();
        var provisionedJson = await provisioned.Content.ReadAsStringAsync();
        Assert.DoesNotContain(firstCredential, provisionedJson, StringComparison.Ordinal);
        Assert.True((await ReadDataAsync(provisioned)).GetProperty("credentialProvisioned").GetBoolean());

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            var stored = await secrets.LoadAsync(new SecretStoreAddress(
                SlackDeliveryOwnerIds.ManagerProjectId,
                credentialRef,
                SecretKind.BotToken));
            Assert.Equal(firstCredential, Encoding.UTF8.GetString(stored!));
            Assert.Null(await secrets.LoadAsync(new SecretStoreAddress(
                SlackDeliveryOwnerIds.ManagerProjectId,
                "caller-supplied-reference-must-not-be-used",
                SecretKind.BotToken)));
        }

        using var status = await _fixture.Client.GetAsync($"/api/slack-manager/status?workspaceTeamId={team}");
        status.EnsureSuccessStatusCode();
        var statusJson = await status.Content.ReadAsStringAsync();
        Assert.DoesNotContain(firstCredential, statusJson, StringComparison.Ordinal);
        var statusData = await ReadDataAsync(status);
        Assert.True(statusData.GetProperty("enrollment").GetProperty("managerCredentialProvisioned").GetBoolean());
        Assert.Equal("claim_manager", statusData.GetProperty("nextAction").GetString());

        using var rotated = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/credentials", new
        {
            workspaceTeamId = team,
            managerBotToken = rotatedCredential,
        });
        rotated.EnsureSuccessStatusCode();
        Assert.DoesNotContain(rotatedCredential, await rotated.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var verify = _fixture.Services.CreateAsyncScope();
        var rotatedSecret = await verify.ServiceProvider.GetRequiredService<ISecretStore>().LoadAsync(
            new SecretStoreAddress(
                SlackDeliveryOwnerIds.ManagerProjectId,
                credentialRef,
                SecretKind.BotToken));
        Assert.Equal(rotatedCredential, Encoding.UTF8.GetString(rotatedSecret!));
    }

    [Fact]
    public async Task Manager_credential_provisioning_rejects_non_loopback_operator_requests()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/slack-manager/credentials")
        {
            Content = JsonContent.Create(new
            {
                workspaceTeamId = "T_MANAGER_NON_LOOPBACK",
                managerBotToken = "manager-credential-non-loopback",
            }),
        };
        request.Headers.Add("X-Test-Remote-Address", "203.0.113.10");

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("loopback_required", error.RootElement.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
