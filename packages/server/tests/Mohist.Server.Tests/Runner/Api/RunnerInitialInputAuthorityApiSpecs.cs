using System.Net;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Runner.Api;

[Collection("RunnerMutationIntegration")]
[Trait("level", "L1")]
public sealed class RunnerInitialInputAuthorityApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerInitialInputAuthorityApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task InitialInputMutationRoutesRejectOldClosingAndDrainingRunnerAuthority()
    {
        await AssertAllMutationRoutesRejectedAsync("old", async (runner, runnerId) =>
        {
            await runner.RegisterAsync(RunnerInfoFor(runnerId), "current-process");
            return "old-process";
        });
        await AssertAllMutationRoutesRejectedAsync("draining", async (runner, runnerId) =>
        {
            await runner.RegisterAsync(RunnerInfoFor(runnerId), "current-process");
            await runner.BeginDrainAsync();
            return "current-process";
        });
        await AssertAllMutationRoutesRejectedAsync("closing", async (runner, runnerId) =>
        {
            await runner.RegisterAsync(RunnerInfoFor(runnerId), "current-process");
            await runner.UnregisterAsync();
            return "current-process";
        });
    }

    private async Task AssertAllMutationRoutesRejectedAsync(
        string scenario,
        Func<IRunnerGrain, string, Task<string>> arrange)
    {
        var runnerId = $"initial-input-authority-{scenario}-{Guid.NewGuid():N}";
        var jobId = $"initial-input-authority-job-{scenario}-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var processGeneration = await arrange(runner, runnerId);

        foreach (var mutation in new[] { "prepare", "complete", "start" })
        {
            using var response = await PostMutationAsync(runnerId, jobId, mutation, processGeneration);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("runner_process_stale", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(AgentJobStatus.Pending, await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).GetStatusAsync());
    }

    private Task<HttpResponseMessage> PostMutationAsync(
        string runnerId,
        string jobId,
        string mutation,
        string processGeneration)
    {
        object body = mutation switch
        {
            "start" => new
            {
                operationId = "operation-1",
                submissionAttemptId = "submission-attempt-1",
                workId = "work-1",
                processGeneration,
                sessionId = "session-1",
                inputId = "input-1",
                turnId = "turn-1",
                runtime = "opencode",
                runtimeSessionId = "runtime-old",
            },
            "complete" => new
            {
                operationId = "operation-1",
                workId = "work-1",
                processGeneration,
                sessionId = "session-1",
                inputId = "input-1",
                turnId = "turn-1",
                expectedRuntime = "opencode",
                expectedRuntimeSessionId = "runtime-old",
                creationAttemptId = "creation-attempt-1",
                recoveryReason = AgentJobInitialRecoveryReasons.SameRuntimeMissing,
                replacementRuntime = "opencode",
                replacementRuntimeSessionId = "runtime-new",
            },
            _ => new
            {
                operationId = "operation-1",
                workId = "work-1",
                processGeneration,
                sessionId = "session-1",
                inputId = "input-1",
                turnId = "turn-1",
                expectedRuntime = "opencode",
                expectedRuntimeSessionId = "runtime-old",
                creationAttemptId = "creation-attempt-1",
                recoveryReason = AgentJobInitialRecoveryReasons.SameRuntimeMissing,
            },
        };
        var suffix = mutation == "start" ? "start" : $"recovery/{mutation}";
        return _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/agent-jobs/{jobId}/initial-input/{suffix}",
            body);
    }

    private static RunnerInfo RunnerInfoFor(string runnerId) =>
        new(runnerId, ["spec/*"], $"{runnerId}-host", ProjectId: $"{runnerId}-project");
}
