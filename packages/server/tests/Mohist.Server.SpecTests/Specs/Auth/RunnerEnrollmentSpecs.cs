using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Auth;

/// <summary>
/// Runner install registration (docs/auth.md "Runner：安装即注册"): a
/// fresh runner registers through a one-time, 15-minute enrollment token
/// and receives a machine credential bound to its RunnerId; revocation
/// rejects that runner's requests immediately while others keep working;
/// re-running the install flow restores a revoked runner.
/// </summary>
[Collection("WorkflowRuntimeIntegration")]
public sealed class RunnerEnrollmentSpecs(IsolatedMohistIntegrationFixture fixture)
{
    private const string EnrollmentTokensPath = "/api/runners/enrollment-tokens";
    private const string RegisterPath = "/api/runners/register";

    [Fact]
    public async Task InstallRunner_RegistersTheRunner_AndItsOwnCredentialWorks()
    {
        var enrollmentToken = await CreateEnrollmentTokenAsync();

        using var register = await fixture.Client.PostAsJsonAsync(RegisterPath, new
        {
            token = enrollmentToken,
            runnerId = "runner-installed",
            hostname = "host-1",
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var body = await register.Content.ReadAsStringAsync();
        var credential = JsonDocument.Parse(body).RootElement
            .GetProperty("data").GetProperty("token").GetString()!;
        Assert.StartsWith("moh_runner_", credential, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(body, Regex.Escape(credential)));

        // The machine credential alone (no shared deployment token) is
        // enough for the runner's own endpoints.
        using var runnerClient = fixture.CreateClient();
        runnerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var config = await runnerClient.GetAsync("/api/runner/runner-installed/config");
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);

        // An anonymous request is still rejected: the runner no longer
        // relies on any shared credential.
        using var anonymous = fixture.CreateClient();
        using var rejected = await anonymous.GetAsync("/api/runner/runner-installed/config");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task RevokingACredential_RejectsThatRunner_WhileOtherRunnersKeepWorking()
    {
        var runnerA = await RegisterAsync(await CreateEnrollmentTokenAsync(), "runner-revoke-a");
        var runnerB = await RegisterAsync(await CreateEnrollmentTokenAsync(), "runner-revoke-b");

        using var revoke = await fixture.Client.DeleteAsync($"/api/runners/runner-revoke-a/credentials");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        using var revokedClient = fixture.CreateClient();
        revokedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", runnerA);
        using var revokedCall = await revokedClient.GetAsync("/api/runner/runner-revoke-a/config");
        Assert.Equal(HttpStatusCode.Unauthorized, revokedCall.StatusCode);

        using var survivorClient = fixture.CreateClient();
        survivorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", runnerB);
        using var survivorCall = await survivorClient.GetAsync("/api/runner/runner-revoke-b/config");
        Assert.Equal(HttpStatusCode.OK, survivorCall.StatusCode);
    }

    [Fact]
    public async Task Reinstalling_AfterRevoke_RestoresTheRunner()
    {
        var oldCredential = await RegisterAsync(await CreateEnrollmentTokenAsync(), "runner-reinstall");

        await fixture.Client.DeleteAsync("/api/runners/runner-reinstall/credentials");

        var newCredential = await RegisterAsync(await CreateEnrollmentTokenAsync(), "runner-reinstall");
        Assert.NotEqual(oldCredential, newCredential);

        using var restoredClient = fixture.CreateClient();
        restoredClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newCredential);
        using var restored = await restoredClient.GetAsync("/api/runner/runner-reinstall/config");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        using var staleClient = fixture.CreateClient();
        staleClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldCredential);
        using var stale = await staleClient.GetAsync("/api/runner/runner-reinstall/config");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [Fact]
    public async Task EnrollmentToken_CreationRequiresAuthentication()
    {
        using var anonymous = fixture.CreateClient();

        using var response = await anonymous.PostAsJsonAsync(EnrollmentTokensPath, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_RequiresAuthentication()
    {
        using var anonymous = fixture.CreateClient();

        using var response = await anonymous.DeleteAsync("/api/runners/runner-anon/credentials");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EnrollmentToken_IsNotACredential_AndCannotBeUsedAsBearer()
    {
        var enrollmentToken = await CreateEnrollmentTokenAsync();

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrollmentToken);
        using var response = await client.GetAsync("/api/runner/runner-whatever/config");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_UnknownRunner_Returns404()
    {
        using var response = await fixture.Client.DeleteAsync("/api/runners/runner-missing/credentials");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<string> CreateEnrollmentTokenAsync()
    {
        using var response = await fixture.Client.PostAsJsonAsync(EnrollmentTokensPath, new { });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        var token = data.GetProperty("token").GetString()!;
        Assert.StartsWith("moh_enroll_", token, StringComparison.Ordinal);
        Assert.Equal(
            fixture.TimeProvider.GetUtcNow().AddMinutes(15),
            data.GetProperty("expiresAt").GetDateTimeOffset());
        return token;
    }

    private async Task<string> RegisterAsync(string enrollmentToken, string runnerId)
    {
        using var response = await fixture.Client.PostAsJsonAsync(RegisterPath, new
        {
            token = enrollmentToken,
            runnerId,
        });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }
}
