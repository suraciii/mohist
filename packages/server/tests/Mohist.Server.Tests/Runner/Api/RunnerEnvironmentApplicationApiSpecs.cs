using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Runner.Api;

[Collection("RunnerConfig")]
[Trait("level", "L1")]
public sealed class RunnerEnvironmentApplicationApiSpecs : IAsyncLifetime
{
    private readonly RunnerConfigFixture _fixture;

    public RunnerEnvironmentApplicationApiSpecs(RunnerConfigFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => new(_fixture.UnregisterRunnersAsync());

    [Fact]
    public async Task ApplicationRoutesExposeWaitingStateAndExactCancellation()
    {
        var runnerId = await _fixture.RegisterRunnerAsync();
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var info = Assert.IsType<RunnerInfo>(await runner.GetInfoAsync());
        var updateId = Guid.NewGuid().ToString("N");

        using (var begin = await _fixture.Client.PostAsJsonAsync(
                   $"/api/runner/{runnerId}/environment/application",
                   new
                   {
                       updateId,
                       targetVersion = "candidate-version",
                       processGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
                       connectionGeneration = info.ConnectionGeneration,
                   }))
        {
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
            var data = (await begin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("waiting", data.GetProperty("status").GetString());
            Assert.Equal("waiting", data.GetProperty("application").GetProperty("phase").GetString());
        }

        using (var conflict = await _fixture.Client.PostAsJsonAsync(
                   $"/api/runner/{runnerId}/environment/application",
                   new
                   {
                       updateId = Guid.NewGuid().ToString("N"),
                       targetVersion = "other-version",
                       processGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
                       connectionGeneration = info.ConnectionGeneration,
                   }))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        using (var get = await _fixture.Client.GetAsync(
                   $"/api/runner/{runnerId}/environment/application/{updateId}"))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var data = (await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal(updateId, data.GetProperty("updateId").GetString());
            Assert.Equal("accepted", data.GetProperty("status").GetString());
        }

        using (var cancel = await _fixture.Client.PostAsync(
                   $"/api/runner/{runnerId}/environment/application/{updateId}/cancel",
                   content: null))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            var data = (await cancel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("cancelled", data.GetProperty("status").GetString());
            Assert.Equal("cancelled", data.GetProperty("application").GetProperty("phase").GetString());
        }
    }

    [Fact]
    public async Task ApplicationRoutesRequireCurrentSettlementAndActivationWitness()
    {
        var runnerId = await _fixture.RegisterRunnerAsync();
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var info = Assert.IsType<RunnerInfo>(await runner.GetInfoAsync());
        var processGeneration = TestRunnerGenerationExtensions.ProcessGeneration;
        var connectionGeneration = Assert.IsType<string>(info.ConnectionGeneration);
        var updateId = Guid.NewGuid().ToString("N");

        await runner.ObserveDispatchObservationAsync(
            processGeneration,
            new RunnerDispatchObservation(
                connectionGeneration,
                AdmissionReady: true,
                AdmissionReasonCodes: [],
                RuntimeReadiness: [],
                ProcessGeneration: processGeneration));
        await runner.BeginEnvironmentApplicationAsync(updateId, "candidate-version", processGeneration, connectionGeneration);

        using (var apply = await _fixture.Client.PostAsJsonAsync(
                   $"/api/runner/{runnerId}/environment/application/{updateId}/apply",
                   new { processGeneration }))
        {
            Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
            var data = (await apply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("accepted", data.GetProperty("status").GetString());
            Assert.Equal("applying", data.GetProperty("application").GetProperty("phase").GetString());
        }

        var replacementGeneration = $"replacement-{Guid.NewGuid():N}";
        var replacementConnection = $"connection-{Guid.NewGuid():N}";
        await runner.RegisterAsync(
            info with
            {
                EnvironmentVersion = "candidate-version",
                ConnectionGeneration = replacementConnection,
            },
            replacementGeneration);

        using (var staleConfirm = await _fixture.Client.PostAsJsonAsync(
                   $"/api/runner/{runnerId}/environment/application/{updateId}/confirm",
                   new { processGeneration, environmentVersion = "candidate-version" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleConfirm.StatusCode);
            var data = (await staleConfirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("details");
            Assert.Equal("stale", data.GetProperty("status").GetString());
        }

        using (var confirm = await _fixture.Client.PostAsJsonAsync(
                   $"/api/runner/{runnerId}/environment/application/{updateId}/confirm",
                   new { processGeneration = replacementGeneration, environmentVersion = "candidate-version" }))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            var data = (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("accepted", data.GetProperty("status").GetString());
            Assert.Equal("active", data.GetProperty("application").GetProperty("phase").GetString());
        }

        Assert.False((await runner.GetRuntimeStateAsync()).Draining);
    }
}
