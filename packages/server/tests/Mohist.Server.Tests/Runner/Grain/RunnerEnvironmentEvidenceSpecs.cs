using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Runner.Grain;

[Collection("ComponentGrain")]
[Trait("level", "L0")]
public sealed class RunnerEnvironmentEvidenceSpecs
{
    private static readonly DateTimeOffset LoadedAt = new(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);
    private readonly ComponentWorkflowGrainFixture _fixture;

    public RunnerEnvironmentEvidenceSpecs(ComponentWorkflowGrainFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RegisterAndHeartbeat_RetainEnvironmentIdentityFacts()
    {
        var runnerId = $"runner-environment-identity-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        try
        {
            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "environment-host",
                    null,
                    EnvironmentVersion: "version-a",
                    EnvironmentLoadedAt: LoadedAt),
                "generation-a");

            var registered = await runner.GetInfoAsync();
            Assert.Equal("version-a", registered?.EnvironmentVersion);
            Assert.Equal(LoadedAt, registered?.EnvironmentLoadedAt);

            await runner.HeartbeatRepairAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "environment-host",
                    null,
                    EnvironmentVersion: "version-b",
                    EnvironmentLoadedAt: LoadedAt.AddMinutes(1)));

            var repaired = await runner.GetInfoAsync();
            Assert.Equal("version-b", repaired?.EnvironmentVersion);
            Assert.Equal(LoadedAt.AddMinutes(1), repaired?.EnvironmentLoadedAt);
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task PollObservation_IsBoundToTheCurrentGenerationAndTracksSettlementCounts()
    {
        var runnerId = $"runner-environment-settlement-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        try
        {
            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "settlement-host",
                    null,
                    ConnectionGeneration: "connection-a"),
                "generation-a");

            var observed = await runner.ObserveDispatchObservationAsync(
                "generation-a",
                new RunnerDispatchObservation(
                    "connection-a",
                    AdmissionReady: true,
                    AdmissionReasonCodes: [],
                    RuntimeReadiness: [],
                    ProcessGeneration: "generation-a",
                    InFlightCount: 2,
                    AwaitingAckCount: 1));

            Assert.NotNull(observed);
            Assert.Equal("generation-a", observed!.ProcessGeneration);
            Assert.Equal(2, observed.InFlightCount);
            Assert.Equal(1, observed.AwaitingAckCount);
            Assert.False(observed.IsSettledFor("generation-a"));

            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "settlement-host",
                    null,
                    ConnectionGeneration: "connection-b"),
                "generation-b");

            Assert.Null((await runner.GetRuntimeStateAsync()).DispatchObservation);
            Assert.Null(await runner.ObserveDispatchObservationAsync(
                "generation-a",
                new RunnerDispatchObservation(
                    "connection-a",
                    AdmissionReady: true,
                    AdmissionReasonCodes: [],
                    RuntimeReadiness: [],
                    ProcessGeneration: "generation-a")));

            var settled = await runner.ObserveDispatchObservationAsync(
                "generation-b",
                new RunnerDispatchObservation(
                    "connection-b",
                    AdmissionReady: true,
                    AdmissionReasonCodes: [],
                    RuntimeReadiness: [],
                    ProcessGeneration: "generation-b"));
            Assert.NotNull(settled);
            Assert.True(settled!.IsSettledFor("generation-b"));
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }
}
