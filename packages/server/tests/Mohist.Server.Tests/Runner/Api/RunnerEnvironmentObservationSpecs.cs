using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Runner.Api;

[Collection("RunnerConfig")]
[Trait("level", "L1")]
public sealed class RunnerEnvironmentObservationSpecs : IAsyncLifetime
{
    private readonly RunnerConfigFixture _fixture;

    public RunnerEnvironmentObservationSpecs(RunnerConfigFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => new(_fixture.UnregisterRunnersAsync());

    [Fact]
    public async Task GrainStoresSanitizedObservationAndRejectsStaleGeneration()
    {
        var runnerId = await _fixture.RegisterRunnerAsync();
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var now = DateTimeOffset.Parse("2026-09-18T01:02:03Z");

        var accepted = await runner.RecordEnvironmentObservationAsync(new RunnerEnvironmentObservation
        {
            CandidateSource = "terminal",
            CandidateUser = "runner-user",
            CandidateVersion = "candidate-v1",
            CandidateVariables = ["PATH", "GOROOT"],
            CandidateCapturedAt = now,
            CandidateAddedVariables = ["GOROOT"],
            ToolChecks =
            [
                new RunnerEnvironmentToolCheck
                {
                    Executable = "go",
                    ResolvedPath = "/opt/go/bin/go",
                    SnapshotKind = "candidate",
                    SnapshotVersion = "candidate-v1",
                    Outcome = "passed",
                    ExitCode = 0,
                    DurationMilliseconds = 12,
                    CheckedAt = now,
                },
            ],
        });

        Assert.Equal(RunnerEnvironmentObservationStatus.Accepted, accepted.Status);
        Assert.Equal("candidate-v1", accepted.Observation!.CandidateVersion);
        Assert.Equal("/opt/go/bin/go", accepted.Observation.ToolChecks.Single().ResolvedPath);

        var stale = await runner.RecordEnvironmentObservationAsync(new RunnerEnvironmentObservation
        {
            ProcessGeneration = "old-process-generation",
            EnvironmentVersion = "old-version",
        });

        Assert.Equal(RunnerEnvironmentObservationStatus.Stale, stale.Status);
        Assert.Equal("candidate-v1", stale.Observation!.CandidateVersion);
        Assert.DoesNotContain("PATH=", JsonSerializer.Serialize(stale.Observation));
    }

    [Fact]
    public async Task ApiPublishesAndReadsSanitizedObservation()
    {
        var runnerId = await _fixture.RegisterRunnerAsync();
        var processGeneration = TestRunnerGenerationExtensions.ProcessGeneration;
        var loadedAt = DateTimeOffset.Parse("2026-09-18T01:00:00Z");

        using (var post = await _fixture.Client.PostAsJsonAsync(
                   $"/api/runner/{runnerId}/environment/observation",
                   new
                   {
                       processGeneration,
                       environmentVersion = "active-v1",
                       environmentLoadedAt = loadedAt,
                       candidate = new
                       {
                           source = "terminal",
                           user = "runner-user",
                           version = "candidate-v2",
                           variables = new[] { "PATH", "GOROOT" },
                           capturedAt = loadedAt,
                           addedVariables = new[] { "GOROOT" },
                       },
                       toolChecks = new[]
                       {
                           new
                           {
                               executable = "go",
                               resolvedPath = "/opt/go/bin/go",
                               snapshotKind = "active",
                               snapshotVersion = "active-v1",
                               outcome = "passed",
                               exitCode = 0,
                               durationMilliseconds = 7,
                               checkedAt = loadedAt,
                           },
                       },
                   }))
        {
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            var data = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("accepted", data.GetProperty("status").GetString());
            Assert.Equal("candidate-v2", data.GetProperty("observation").GetProperty("candidateVersion").GetString());
            Assert.Equal("/opt/go/bin/go", data.GetProperty("observation").GetProperty("toolChecks")[0].GetProperty("resolvedPath").GetString());
            Assert.DoesNotContain("PATH=", data.ToString());
        }

        using (var get = await _fixture.Client.GetAsync(
                   $"/api/runner/{runnerId}/environment/observation"))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var data = (await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal(runnerId, data.GetProperty("runnerId").GetString());
            Assert.Equal("candidate-v2", data.GetProperty("observation").GetProperty("candidateVersion").GetString());
        }

        using (var stale = await _fixture.Client.PostAsJsonAsync(
                   $"/api/runner/{runnerId}/environment/observation",
                   new { processGeneration = "stale-process", environmentVersion = "old" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var payload = await stale.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("environment_observation_stale", payload.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task GrainRejectsMoreThanEightToolChecks()
    {
        var runnerId = await _fixture.RegisterRunnerAsync();
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var checkedAt = DateTimeOffset.Parse("2026-09-18T01:02:03Z");

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RecordEnvironmentObservationAsync(new RunnerEnvironmentObservation
            {
                ToolChecks = Enumerable.Range(0, 9)
                    .Select(index => new RunnerEnvironmentToolCheck
                    {
                        Executable = $"tool-{index}",
                        SnapshotKind = "active",
                        Outcome = "passed",
                        DurationMilliseconds = 1,
                        CheckedAt = checkedAt,
                    })
                    .ToArray(),
            }));

        Assert.Contains("too many tool checks", error.Message);
    }
}
