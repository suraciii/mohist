using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

[Collection("RunnerMutationIntegration")]
public class RunnerIdentityConnectionSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerIdentityConnectionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunnerIdentity_WhenHeartbeatReportsConnectionId_ShowsConnected()
    {
        var runnerId = $"identity-connection-{Guid.NewGuid():N}";
        var hostname = $"identity-host-{Guid.NewGuid():N}";

        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname,
        });

        try
        {
            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/heartbeat", new
            {
                capabilities = new[] { "spec/*" },
                hostname,
                connectionId = "connection-1",
            });

            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/heartbeat", new
            {
                capabilities = new[] { "spec/*" },
                hostname,
            });

            var identity = await _fixture.Client.GetDataAsync<RunnerIdentityDto>(
                $"/api/runner/identity?hostname={Uri.EscapeDataString(hostname)}");

            Assert.Equal(runnerId, identity.RunnerId);
            Assert.Equal(hostname, identity.Hostname);
            Assert.Equal("online", identity.Status);
            Assert.Equal("connected", identity.ConnectionState);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private sealed record RunnerIdentityDto(
        string RunnerId,
        string Hostname,
        string? BuildGitHash,
        string Status,
        DateTimeOffset? LastHeartbeatAt,
        string ConnectionState);
}
