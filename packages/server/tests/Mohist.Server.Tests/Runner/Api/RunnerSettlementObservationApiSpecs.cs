using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Runner.Api;

[Collection("RunnerMutationIntegration")]
[Trait("level", "L1")]
public sealed class RunnerSettlementObservationApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerSettlementObservationApiSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DrainingPoll_StillRecordsCurrentGenerationSettlementCounts()
    {
        var runnerId = $"runner-settlement-observation-{Guid.NewGuid():N}";
        var connectionId = "settlement-connection";
        var processGeneration = $"settlement-generation-{Guid.NewGuid():N}";
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var connectionGeneration = tracker.Register(runnerId, connectionId);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var updateId = Guid.NewGuid().ToString();

        try
        {
            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "settlement-host",
                    null,
                    ConnectionGeneration: connectionGeneration),
                processGeneration);
            var fence = await runner.BeginUpdateInterruptAsync(updateId);
            Assert.NotNull(fence);

            using var response = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/poll",
                new
                {
                    processGeneration,
                    connectionId,
                    inFlight = new[] { "work-a", "work-a", "work-b" },
                    awaitingAck = new[] { "work-c" },
                    admissionReady = true,
                    admissionReasonCodes = Array.Empty<string>(),
                    runtimeReadiness = Array.Empty<object>(),
                });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var observation = (await runner.GetRuntimeStateAsync()).DispatchObservation;
            Assert.NotNull(observation);
            Assert.Equal(processGeneration, observation!.ProcessGeneration);
            Assert.Equal(2, observation.InFlightCount);
            Assert.Equal(1, observation.AwaitingAckCount);
            Assert.False(observation.IsSettledFor(processGeneration));
        }
        finally
        {
            tracker.Unregister(runnerId, connectionId);
            await runner.UnregisterAsync();
        }
    }
}
