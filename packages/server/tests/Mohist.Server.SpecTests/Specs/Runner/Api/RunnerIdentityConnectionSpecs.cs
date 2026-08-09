using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

[Collection("IntegrationRunner")]
public class RunnerIdentityConnectionSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerIdentityConnectionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunnerIdentity_WhenExactRuntimeReportAndConnectionMatch_ShowsConnected()
    {
        var runnerId = $"identity-connection-{Guid.NewGuid():N}";
        const string generation = "managed-generation";
        const string runtimeSessionToken = "managed-session";
        const string sourceHash = "0123456789abcdef0123456789abcdef01234567";
        const string artifactDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();

        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "identity-host",
        });

        try
        {
            tracker.ReportRuntime(runnerId, generation, sourceHash, artifactDigest, runtimeSessionToken);
            tracker.Register(runnerId, "connection-1", generation, sourceHash, artifactDigest, runtimeSessionToken);

            var identity = await _fixture.Client.GetDataAsync<RunnerIdentityDto>(
                $"/api/runner/identity?runnerId={Uri.EscapeDataString(runnerId)}&generation={Uri.EscapeDataString(generation)}");

            Assert.Equal(runnerId, identity.RunnerId);
            Assert.Equal(generation, identity.RuntimeGeneration);
            Assert.Equal(sourceHash, identity.BuildGitHash);
            Assert.Equal(artifactDigest, identity.ArtifactDigest);
            Assert.Equal("online", identity.Status);
            Assert.Equal("connected", identity.ConnectionState);
        }
        finally
        {
            tracker.UnregisterAndGetSessions(runnerId, generation, "connection-1", runtimeSessionToken);
            tracker.UnregisterRuntime(runnerId, generation, runtimeSessionToken);
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task ManagedRegisterAndHeartbeat_WithMissingOrInvalidIdentity_FailBeforeRunnerGrainIsOnline()
    {
        var runnerId = $"identity-invalid-{Guid.NewGuid():N}";
        const string sourceHash = "0123456789abcdef0123456789abcdef01234567";
        const string sessionToken = "managed-session";

        using var registration = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "identity-host",
            runtimeGeneration = "2",
            runtimeSessionToken = sessionToken,
            buildGitHash = sourceHash,
            artifactDigest = (string?)null,
        });

        Assert.Equal(HttpStatusCode.Conflict, registration.StatusCode);
        Assert.Null(await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).GetInfoAsync());

        using var heartbeat = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/heartbeat", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "identity-host",
            runtimeGeneration = "2",
            runtimeSessionToken = sessionToken,
            buildGitHash = sourceHash,
            artifactDigest = "not-a-sha256-digest",
        });

        Assert.Equal(HttpStatusCode.Conflict, heartbeat.StatusCode);
        Assert.Null(await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).GetInfoAsync());
    }

    private sealed record RunnerIdentityDto(
        string RunnerId,
        string RuntimeGeneration,
        string? BuildGitHash,
        string? ArtifactDigest,
        string Status,
        DateTimeOffset? LastHeartbeatAt,
        string ConnectionState);
}
