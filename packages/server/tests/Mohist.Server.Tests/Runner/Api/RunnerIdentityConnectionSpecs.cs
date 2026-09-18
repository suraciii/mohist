using System.Net;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mohist.Server.Tests.Runner.Api;

[Collection("RunnerMutationIntegration")]
[Trait("level", "L1")]
public class RunnerIdentityConnectionSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerIdentityConnectionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunnerIdentity_WhenHeartbeatMatchesControlLease_ShowsConnected()
    {
        var runnerId = $"identity-connection-{Guid.NewGuid():N}";
        var hostname = $"identity-host-{Guid.NewGuid():N}";

        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            processGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
            capabilities = new[] { "spec/*" },
            hostname,
        });

        try
        {
            var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
            tracker.Register(runnerId, "connection-1");
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
            Assert.Equal(TestRunnerGenerationExtensions.ProcessGeneration, identity.ProcessGeneration);
        }
        finally
        {
            _fixture.Services.GetRequiredService<RunnerConnectionTracker>().Unregister(runnerId, "connection-1");
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task RunnerEnvironmentIdentity_RegistrationAndHeartbeatPersistVersionAndLoadTime()
    {
        var runnerId = $"identity-environment-{Guid.NewGuid():N}";
        var processGeneration = $"environment-generation-{Guid.NewGuid():N}";
        var loadedAt = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);

        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            processGeneration,
            capabilities = new[] { "spec/*" },
            hostname = "environment-host",
            environmentVersion = "version-a",
            environmentLoadedAt = loadedAt,
        });

        try
        {
            var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            var registered = await runner.GetInfoAsync();
            Assert.Equal("version-a", registered?.EnvironmentVersion);
            Assert.Equal(loadedAt, registered?.EnvironmentLoadedAt);

            var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
            tracker.Register(runnerId, "environment-connection");
            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/heartbeat", new
            {
                capabilities = new[] { "spec/*" },
                hostname = "environment-host",
                connectionId = "environment-connection",
                environmentVersion = "version-b",
                environmentLoadedAt = loadedAt.AddMinutes(1),
            });

            var repaired = await runner.GetInfoAsync();
            Assert.Equal("version-b", repaired?.EnvironmentVersion);
            Assert.Equal(loadedAt.AddMinutes(1), repaired?.EnvironmentLoadedAt);

            var identity = await _fixture.Client.GetDataAsync<RunnerIdentityDto>(
                "/api/runner/identity?runnerId=" + Uri.EscapeDataString(runnerId));
            Assert.Equal(processGeneration, identity.ProcessGeneration);
            Assert.Equal("version-b", identity.EnvironmentVersion);
            Assert.Equal(loadedAt.AddMinutes(1), identity.EnvironmentLoadedAt);
        }
        finally
        {
            _fixture.Services.GetRequiredService<RunnerConnectionTracker>().Unregister(runnerId, "environment-connection");
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task RunnerIdentity_WithRunnerId_SelectsExactRunnerWhenHostnameIsShared()
    {
        var firstRunnerId = $"identity-exact-first-{Guid.NewGuid():N}";
        var secondRunnerId = $"identity-exact-second-{Guid.NewGuid():N}";
        var hostname = $"identity-shared-host-{Guid.NewGuid():N}";

        await RegisterAsync(firstRunnerId, hostname);
        await RegisterAsync(secondRunnerId, hostname);

        try
        {
            var identity = await _fixture.Client.GetDataAsync<RunnerIdentityDto>(
                $"/api/runner/identity?runnerId={Uri.EscapeDataString(secondRunnerId)}&hostname={Uri.EscapeDataString(hostname)}");

            Assert.Equal(secondRunnerId, identity.RunnerId);
            Assert.Equal(hostname, identity.Hostname);
        }
        finally
        {
            await UnregisterAsync(secondRunnerId);
            await UnregisterAsync(firstRunnerId);
        }
    }

    [Fact]
    public async Task RunnerIdentity_WithUnknownRunnerId_DoesNotFallBackToHostname()
    {
        var knownRunnerId = $"identity-known-{Guid.NewGuid():N}";
        var unknownRunnerId = $"identity-unknown-{Guid.NewGuid():N}";
        var hostname = $"identity-known-host-{Guid.NewGuid():N}";

        await RegisterAsync(knownRunnerId, hostname);

        try
        {
            using var response = await _fixture.Client.GetAsync(
                $"/api/runner/identity?runnerId={Uri.EscapeDataString(unknownRunnerId)}&hostname={Uri.EscapeDataString(hostname)}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await UnregisterAsync(knownRunnerId);
        }
    }

    private Task RegisterAsync(string runnerId, string hostname)
        => _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            processGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
            capabilities = new[] { "spec/*" },
            hostname,
        });

    private Task UnregisterAsync(string runnerId)
        => _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/unregister");

    private sealed record RunnerIdentityDto(
        string RunnerId,
        string Hostname,
        string? BuildGitHash,
        string Status,
        DateTimeOffset? LastHeartbeatAt,
        string ConnectionState,
        string? ProcessGeneration = null,
        string? EnvironmentVersion = null,
        DateTimeOffset? EnvironmentLoadedAt = null);
}
