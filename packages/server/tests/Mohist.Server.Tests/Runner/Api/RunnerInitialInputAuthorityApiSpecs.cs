using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Contracts;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
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

    [Fact]
    public async Task AuthenticatedCredentialCannotBorrowReplacementAuthorityForAnyInitialInputPhase()
    {
        var runnerId = $"initial-input-presented-authority-{Guid.NewGuid():N}";
        var projectId = $"initial-input-project-{Guid.NewGuid():N}";
        var processGeneration = $"process-{Guid.NewGuid():N}";
        var jobId = $"initial-input-job-{Guid.NewGuid():N}";
        var credentialA = await IssueRunnerCredentialAsync(runnerId);
        await RegisterProcessAsync(credentialA.Token, runnerId, projectId, processGeneration);

        var observer = _fixture.Services.GetRequiredService<RunnerAuthorityAdmissionObserver>();
        var allStaleRequestsObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleRequests = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedOperations = new HashSet<string>(StringComparer.Ordinal);
        observer.ObservedAsync = async (operation, observedRunnerId, authority, ct) =>
        {
            if (!string.Equals(observedRunnerId, runnerId, StringComparison.Ordinal)
                || !string.Equals(
                    authority.CredentialId,
                    credentialA.Credential.Id,
                    StringComparison.Ordinal)
                || !operation.StartsWith("initial-input-", StringComparison.Ordinal))
                return;

            lock (observedOperations)
            {
                observedOperations.Add(operation);
                if (observedOperations.Count == 3)
                    allStaleRequestsObserved.TrySetResult();
            }
            await releaseStaleRequests.Task.WaitAsync(ct);
        };

        using var staleClient = RunnerClient(credentialA.Token);
        var staleRequests = new[] { "prepare", "complete", "start" }
            .Select(mutation => PostMutationAsync(
                staleClient,
                runnerId,
                jobId,
                mutation,
                processGeneration))
            .ToArray();

        try
        {
            await allStaleRequestsObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
            var credentialB = await IssueRunnerCredentialAsync(runnerId);
            using var replacementClient = RunnerClient(credentialB.Token);

            using (var beforeRegistration = await PostMutationAsync(
                       replacementClient,
                       runnerId,
                       jobId,
                       "prepare",
                       processGeneration))
            {
                Assert.Equal(HttpStatusCode.Forbidden, beforeRegistration.StatusCode);
                Assert.Contains(
                    "runner_credential_authority_invalid",
                    await beforeRegistration.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }

            await RegisterProcessAsync(
                credentialB.Token,
                runnerId,
                projectId,
                processGeneration);
            releaseStaleRequests.TrySetResult();

            foreach (var staleRequest in staleRequests)
            {
                using var response = await staleRequest;
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Contains(
                    "runner_credential_authority_invalid",
                    await response.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }

            var recovery = await CreateInitialRecoveryClaimAsync(
                runnerId,
                projectId,
                processGeneration,
                jobId);
            using var admitted = await PostMutationAsync(
                replacementClient,
                runnerId,
                jobId,
                "prepare",
                processGeneration,
                recovery);
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
            Assert.Contains(
                "\"candidateCreationAuthorized\":true",
                await admitted.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
        finally
        {
            observer.ObservedAsync = null;
            releaseStaleRequests.TrySetResult();
        }
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
        string processGeneration) =>
        PostMutationAsync(
            _fixture.Client,
            runnerId,
            jobId,
            mutation,
            processGeneration);

    private static Task<HttpResponseMessage> PostMutationAsync(
        HttpClient client,
        string runnerId,
        string jobId,
        string mutation,
        string processGeneration,
        InitialRecoveryClaim? claim = null)
    {
        var operationId = claim?.OperationId ?? "operation-1";
        var workId = claim?.WorkId ?? "work-1";
        var sessionId = claim?.SessionId ?? "session-1";
        var inputId = claim?.InputId ?? "input-1";
        var turnId = claim?.TurnId ?? "turn-1";
        var runtimeSessionId = claim?.RuntimeSessionId ?? "runtime-old";
        var creationAttemptId = claim?.CreationAttemptId ?? "creation-attempt-1";
        object body = mutation switch
        {
            "start" => new
            {
                operationId,
                submissionAttemptId = "submission-attempt-1",
                workId,
                processGeneration,
                sessionId,
                inputId,
                turnId,
                runtime = "opencode",
                runtimeSessionId,
            },
            "complete" => new
            {
                operationId,
                workId,
                processGeneration,
                sessionId,
                inputId,
                turnId,
                expectedRuntime = "opencode",
                expectedRuntimeSessionId = runtimeSessionId,
                creationAttemptId,
                recoveryReason = AgentJobInitialRecoveryReasons.SameRuntimeMissing,
                replacementRuntime = "opencode",
                replacementRuntimeSessionId = "runtime-new",
            },
            _ => new
            {
                operationId,
                workId,
                processGeneration,
                sessionId,
                inputId,
                turnId,
                expectedRuntime = "opencode",
                expectedRuntimeSessionId = runtimeSessionId,
                creationAttemptId,
                recoveryReason = AgentJobInitialRecoveryReasons.SameRuntimeMissing,
            },
        };
        var suffix = mutation == "start" ? "start" : $"recovery/{mutation}";
        return client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/agent-jobs/{jobId}/initial-input/{suffix}",
            body,
            TestContext.Current.CancellationToken);
    }

    private async Task<InitialRecoveryClaim> CreateInitialRecoveryClaimAsync(
        string runnerId,
        string projectId,
        string processGeneration,
        string jobId)
    {
        var sessionId = $"initial-input-session-{Guid.NewGuid():N}";
        var inputId = $"initial-input-{Guid.NewGuid():N}";
        var turnId = $"initial-turn-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-{Guid.NewGuid():N}";
        var workDir = $"/tmp/{jobId}";
        await _fixture.SeedAgentAsync(projectId, "agent-test");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            workDir,
            Metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", projectId)
                .WithLabel("mohist.io/source-kind", "agent-launch")
                .WithLabel("mohist.io/source-id", jobId)
                .WithLabel("mohist.io/agent-id", "agent-test")));
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            inputId,
            turnId,
            "recover me",
            "agent-connection",
            jobId,
            Runtime: "opencode",
            Metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", projectId)
                .WithLabel("mohist.io/source-kind", "agent-launch")
                .WithLabel("mohist.io/source-id", jobId)
                .WithLabel("mohist.io/agent-id", "agent-test"),
            WorkDir: workDir));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            runtimeSessionId,
            WorkDir: workDir,
            Runtime: "opencode"));

        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.SubmitAsync(new AgentJobInput(
            "recover me",
            WorkspacePath: workDir,
            ProjectId: projectId,
            Runtime: "opencode",
            AgentId: "agent-test",
            AgentSessionId: sessionId,
            InitialInputId: inputId,
            InitialTurnId: turnId,
            PinnedRunnerId: runnerId));
        var claim = await job.ClaimNextAsync(runnerId, processGeneration);
        Assert.NotNull(claim);
        Assert.True(await job.RecordRuntimeSessionBindingAsync(
            runnerId,
            claim.WorkId,
            sessionId,
            runtimeSessionId));

        return new InitialRecoveryClaim(
            $"operation-{Guid.NewGuid():N}",
            claim.WorkId,
            sessionId,
            inputId,
            turnId,
            runtimeSessionId,
            $"creation-attempt-{Guid.NewGuid():N}");
    }

    private async Task<RunnerCredentialCreateResult> IssueRunnerCredentialAsync(string runnerId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICredentialStore>()
            .CreateRunnerCredentialAsync(MohistPrincipal.AdminPrincipalId, runnerId);
        return Assert.IsType<RunnerCredentialCreateResult>(result);
    }

    private async Task RegisterProcessAsync(
        string token,
        string runnerId,
        string projectId,
        string processGeneration)
    {
        using var client = RunnerClient(token);
        using var response = await client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/register",
            new
            {
                capabilities = new[] { "spec/*", AgentExecutionSources.Version1Capability },
                hostname = $"{runnerId}-host",
                projectId,
                processGeneration,
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpClient RunnerClient(string token)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static RunnerInfo RunnerInfoFor(string runnerId) =>
        new(runnerId, ["spec/*"], $"{runnerId}-host", ProjectId: $"{runnerId}-project");

    private sealed record InitialRecoveryClaim(
        string OperationId,
        string WorkId,
        string SessionId,
        string InputId,
        string TurnId,
        string RuntimeSessionId,
        string CreationAttemptId);
}
