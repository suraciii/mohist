using System.Net;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Auth;

/// <summary>
/// The legacy <c>X-Mohist-Operator-Token</c> header is retired: requests
/// carrying only it are rejected by the auth middleware on every face —
/// dead letters, Slack control plane, and Slack adapter leases — exactly
/// like requests with no credential at all. A valid Bearer credential
/// makes the legacy header irrelevant.
/// </summary>
[Collection("IntegrationMisc")]
public sealed class OperatorCredentialMigrationSpecs(MohistIntegrationFixture fixture)
{
    private const string LegacyHeaderName = "X-Mohist-Operator-Token";

    [Fact]
    public async Task LegacyHeader_OnDeadLetterRoutes_IsRejected()
    {
        using var response = await SendLegacyOnlyAsync(HttpMethod.Get, "/api/events/dead-letters/");

        AssertUnauthorized(response);
    }

    [Fact]
    public async Task LegacyHeader_OnSlackControlPlane_IsRejected()
    {
        using var response = await SendLegacyOnlyAsync(
            HttpMethod.Get,
            "/api/slack-manager/status?workspaceTeamId=T_MIGRATE");

        AssertUnauthorized(response);
    }

    [Fact]
    public async Task LegacyHeader_OnSlackAdapterLeases_IsRejected()
    {
        using var response = await SendLegacyOnlyAsync(
            HttpMethod.Post,
            "/api/slack-adapter/leases/targets");

        AssertUnauthorized(response);
    }

    [Fact]
    public async Task LegacyHeader_NextToAValidBearer_IsIgnored()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MohistIntegrationFixture.AdminToken);
        client.DefaultRequestHeaders.Add(LegacyHeaderName, MohistIntegrationFixture.OperatorToken);

        using var response = await client.GetAsync("/api/events/dead-letters/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendLegacyOnlyAsync(HttpMethod method, string path)
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(LegacyHeaderName, MohistIntegrationFixture.OperatorToken);
        using var request = new HttpRequestMessage(method, path);
        return await client.SendAsync(request);
    }

    private static void AssertUnauthorized(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "Bearer error=\"invalid_token\"",
            Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }
}
