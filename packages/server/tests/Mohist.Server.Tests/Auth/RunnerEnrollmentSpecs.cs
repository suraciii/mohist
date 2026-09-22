using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Runner.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.WebSocket;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Orleans.Reminders;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Mohist.Server.Tests.Auth;

/// <summary>
/// Runner install registration (docs/auth.md "Runner：安装即注册"): a
/// fresh runner registers through a one-time, 15-minute enrollment token
/// and receives a machine credential bound to its RunnerId; revocation
/// rejects that runner's requests immediately while others keep working;
/// re-running the install flow restores a revoked runner.
/// </summary>
[Collection("WorkflowRuntimeIntegration")]
[Trait("level", "L1")]
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
    public async Task RevocationFencesDurableProcessAuthorityUntilSameRunnerReenrolls()
    {
        var runnerId = $"runner-authority-{Guid.NewGuid():N}";
        var oldCredential = await RegisterAsync(await CreateEnrollmentTokenAsync(), runnerId);
        var runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await RegisterProcessAsync(oldCredential, runnerId, "process-before-revoke");
        Assert.True(await runner.IsCurrentProcessGenerationAsync("process-before-revoke"));

        using var revoke = await fixture.Client.DeleteAsync($"/api/runners/{runnerId}/credentials");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.False(await runner.IsCurrentProcessGenerationAsync("process-before-revoke"));
        Assert.False((await runner.TryBeginPollAsync("process-before-revoke")).Admitted);
        Assert.True((await runner.GetRuntimeStateAsync()).Draining);

        await TestLifecycle.DeactivateAndWait(runner, fixture.Grains);
        runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.False(await runner.IsCurrentProcessGenerationAsync("process-before-revoke"));

        var replacementCredential = await RegisterAsync(await CreateEnrollmentTokenAsync(), runnerId);
        await RegisterProcessAsync(replacementCredential, runnerId, "process-after-reenroll");
        Assert.True(await runner.IsCurrentProcessGenerationAsync("process-after-reenroll"));
        Assert.False((await runner.GetRuntimeStateAsync()).Draining);
    }

    [Fact]
    public async Task PartialRemovalImmediatelyClosesAllControlAuthorityUntilCredentialBRegisters()
    {
        var runnerId = $"runner-partial-removal-{Guid.NewGuid():N}";
        var credentialA = await RegisterAsync(await CreateEnrollmentTokenAsync(), runnerId);
        await RegisterProcessAsync(credentialA, runnerId, "process-a");
        var runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var failures = fixture.Services.GetRequiredService<RunnerCredentialRevocationFailureProbe>();
        failures.FailNext(runnerId);

        using var oldSocket = await RunnerControlClient(credentialA, Guid.NewGuid()).ConnectAsync(
            ControlUri(runnerId, "process-a"),
            TestContext.Current.CancellationToken);
        var registry = fixture.Services.GetRequiredService<RunnerControlWebSocketRegistry>();
        await registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.RevokeExecutionAuthorityAsync(fixture.TimeProvider.GetUtcNow()));

            var storage = fixture.Services.GetRequiredService<IGrainStorage>();
            var state = new GrainState<RunnerState>();
            await storage.ReadStateAsync("runner", runner.GetGrainId(), state);
            Assert.Equal(
                RunnerAdministrativeRemovalPhase.IntentRecorded,
                state.State.AdministrativeRemoval!.Phase);
            Assert.Equal("process-a", state.State.CurrentProcessGeneration);
            Assert.NotNull(await fixture.Services.GetRequiredService<IRunnerCredentialStatusReader>()
                .GetActiveAuthorityAsync(runnerId));

            var credentialAId = state.State.CurrentRegistrationCredentialId;
            Assert.False(await runner.IsCurrentRegistrationAuthorityAsync(
                "process-a",
                new RunnerPresentedAuthority(credentialAId, OperatorOverride: false)));
            Assert.False(await runner.IsCurrentRegistrationAuthorityAsync(
                "process-a",
                new RunnerPresentedAuthority(CredentialId: null, OperatorOverride: true)));

            var closed = await oldSocket.ReceiveAsync(
                new byte[64],
                TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
            var requestEnqueued = false;
            await Assert.ThrowsAsync<RunnerControlUnavailableException>(() =>
                registry.SendRequestAsync<WorkspaceQueryParams, WorkspaceRemovalResult>(
                    runnerId,
                    "workspace.remove",
                    new WorkspaceQueryParams(new RunnerWorkspaceQuery(
                        null, null, null, null, null, null, null)),
                    requestEnqueued: () => requestEnqueued = true,
                    ct: TestContext.Current.CancellationToken));
            Assert.False(requestEnqueued);

            await Assert.ThrowsAnyAsync<Exception>(() => RunnerControlClient(
                    credentialA,
                    Guid.NewGuid())
                .ConnectAsync(
                    ControlUri(runnerId, "process-a"),
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAnyAsync<Exception>(() => OperatorControlClient(Guid.NewGuid())
                .ConnectAsync(
                    ControlUri(runnerId, "process-a"),
                    TestContext.Current.CancellationToken));

            var completed = await runner.RevokeExecutionAuthorityAsync(
                fixture.TimeProvider.GetUtcNow());
            Assert.True(completed.Completed);
            var credentialB = await RegisterAsync(await CreateEnrollmentTokenAsync(), runnerId);
            await RegisterProcessAsync(credentialB, runnerId, "process-b");

            using var replacement = await RunnerControlClient(credentialB, Guid.NewGuid()).ConnectAsync(
                ControlUri(runnerId, "process-b"),
                TestContext.Current.CancellationToken);
            await registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);
            Assert.True(registry.IsConnected(runnerId));
            await replacement.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "test complete",
                TestContext.Current.CancellationToken);
        }
        finally
        {
            failures.Reset();
        }
    }

    [Fact]
    public async Task FailedInitialIntentPersistenceAfterFenceDeliversOrdinaryDisconnect()
    {
        var runnerId = $"runner-removal-rollback-{Guid.NewGuid():N}";
        var sessionId = $"session-removal-rollback-{Guid.NewGuid():N}";
        var credential = await RegisterAsync(await CreateEnrollmentTokenAsync(), runnerId);
        await RegisterProcessAsync(credential, runnerId, "process-a");
        var runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var session = fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);

        using var socket = await RunnerControlClient(credential, Guid.NewGuid()).ConnectAsync(
            ControlUri(runnerId, "process-a"),
            TestContext.Current.CancellationToken);
        var registry = fixture.Services.GetRequiredService<RunnerControlWebSocketRegistry>();
        await registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);
        await session.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            WorkDir: "/work",
            Metadata: GenericAgentSessionMetadata.Metadata(
                new GenericAgentSessionContext("project-1", "agent-1", "Agent One"))));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-session-1"));
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "input-1", "turn-1", "prompt", "agent-connection", "job-1"));
        await session.MarkInitialTurnExecutingAsync("job-1");

        var observer = fixture.Services.GetRequiredService<RunnerAdministrativeRemovalObserver>();
        observer.BeforeIntentWriteAsync = (_, _) =>
            Task.FromException(new InvalidOperationException("initial intent write failed"));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.RevokeExecutionAuthorityAsync(fixture.TimeProvider.GetUtcNow()));

            var storage = fixture.Services.GetRequiredService<IGrainStorage>();
            var state = new GrainState<RunnerState>();
            await storage.ReadStateAsync("runner", runner.GetGrainId(), state);
            Assert.Null(state.State.AdministrativeRemoval);
            Assert.Equal("unknown", (await session.GetAsync())!.Status);
            Assert.True(await runner.IsCurrentRegistrationAuthorityAsync(
                "process-a",
                new RunnerPresentedAuthority(
                    state.State.CurrentRegistrationCredentialId,
                    OperatorOverride: false)));

            var closed = await socket.ReceiveAsync(
                new byte[64],
                TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketMessageType.Close, closed.MessageType);

            await runner.AsReference<IRemindable>().ReceiveReminder(
                "administrative-removal",
                default);
            var reminders = fixture.Services.GetRequiredService<IReminderTable>();
            Assert.Null(await reminders.ReadRow(
                runner.GetGrainId(),
                "administrative-removal"));
        }
        finally
        {
            observer.Reset();
        }
    }

    [Fact]
    public async Task PausedCredentialARequestsCannotBorrowReplacementCredentialB()
    {
        var runnerId = $"runner-presented-authority-{Guid.NewGuid():N}";
        var credentialA = await RegisterAsync(await CreateEnrollmentTokenAsync(), runnerId);
        await RegisterProcessAsync(credentialA, runnerId, "process-a");
        using var scope = fixture.Services.CreateScope();
        var credentialAId = Assert.IsType<RunnerCredentialAuthority>(
            await scope.ServiceProvider.GetRequiredService<IRunnerCredentialStatusReader>()
                .GetActiveAuthorityAsync(runnerId)).CredentialId;

        var observer = fixture.Services.GetRequiredService<RunnerAuthorityAdmissionObserver>();
        var firstRegisterObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRegisterObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controlObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRegister = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondRegister = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseControl = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registerCount = 0;
        var captureStaleRequests = 1;
        observer.ObservedAsync = async (operation, observedRunnerId, authority, ct) =>
        {
            if (Volatile.Read(ref captureStaleRequests) == 0
                || !string.Equals(observedRunnerId, runnerId, StringComparison.Ordinal)
                || !string.Equals(authority.CredentialId, credentialAId, StringComparison.Ordinal))
                return;
            if (string.Equals(operation, "control", StringComparison.Ordinal))
            {
                controlObserved.TrySetResult();
                await releaseControl.Task.WaitAsync(ct);
                return;
            }

            var ordinal = Interlocked.Increment(ref registerCount);
            if (ordinal == 1)
            {
                firstRegisterObserved.TrySetResult();
                await releaseFirstRegister.Task.WaitAsync(ct);
            }
            else
            {
                secondRegisterObserved.TrySetResult();
                await releaseSecondRegister.Task.WaitAsync(ct);
            }
        };

        using var staleClient = fixture.CreateClient();
        staleClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credentialA);
        var staleBeforeReplacement = staleClient.PostAsJsonAsync(
            $"/api/runner/{runnerId}/register",
            RegisterBody("stale-before-b"),
            TestContext.Current.CancellationToken);
        Task<HttpResponseMessage>? staleAfterReplacement = null;
        var staleControlClient = fixture.CreateWebSocketClient();
        staleControlClient.ConfigureRequest = request =>
        {
            request.Headers.Authorization = $"Bearer {credentialA}";
            request.Headers["X-Runner-Connection-Id"] = Guid.NewGuid().ToString("D");
        };
        Task<WebSocket>? staleControl = null;

        try
        {
            await firstRegisterObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
            staleAfterReplacement = staleClient.PostAsJsonAsync(
                $"/api/runner/{runnerId}/register",
                RegisterBody("stale-after-b"),
                TestContext.Current.CancellationToken);
            staleControl = staleControlClient.ConnectAsync(
                new Uri($"ws://localhost/api/runner/{runnerId}/control?processGeneration=process-a"),
                TestContext.Current.CancellationToken);
            await Task.WhenAll(
                secondRegisterObserved.Task,
                controlObserved.Task).WaitAsync(TestContext.Current.CancellationToken);
            Volatile.Write(ref captureStaleRequests, 0);

            using var revoke = await fixture.Client.DeleteAsync(
                $"/api/runners/{runnerId}/credentials",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            var credentialB = await RegisterAsync(await CreateEnrollmentTokenAsync(), runnerId);

            releaseFirstRegister.TrySetResult();
            using (var stale = await staleBeforeReplacement)
                Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);

            await RegisterProcessAsync(credentialB, runnerId, "process-b");
            releaseSecondRegister.TrySetResult();
            releaseControl.TrySetResult();

            using (var stale = await staleAfterReplacement!)
                Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
            await Assert.ThrowsAnyAsync<Exception>(() => staleControl!);
            Assert.True(await fixture.Grains.GetGrain<IRunnerGrain>(runnerId)
                .IsCurrentProcessGenerationAsync("process-b"));
        }
        finally
        {
            observer.ObservedAsync = null;
            releaseFirstRegister.TrySetResult();
            releaseSecondRegister.TrySetResult();
            releaseControl.TrySetResult();
        }
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

    private static Uri ControlUri(string runnerId, string processGeneration) => new(
        $"ws://localhost/api/runner/{runnerId}/control?processGeneration={Uri.EscapeDataString(processGeneration)}");

    private Microsoft.AspNetCore.TestHost.WebSocketClient RunnerControlClient(
        string credential,
        Guid connectionId)
    {
        var client = fixture.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Authorization = $"Bearer {credential}";
            request.Headers["X-Runner-Connection-Id"] = connectionId.ToString("D");
        };
        return client;
    }

    private Microsoft.AspNetCore.TestHost.WebSocketClient OperatorControlClient(Guid connectionId)
    {
        var client = fixture.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Authorization = $"Bearer {MohistIntegrationFixture.OperatorToken}";
            request.Headers["X-Runner-Connection-Id"] = connectionId.ToString("D");
        };
        return client;
    }

    private static object RegisterBody(string processGeneration) => new
    {
        processGeneration,
        capabilities = new[] { "spec/*" },
        hostname = "host-1",
    };

    private async Task RegisterProcessAsync(
        string credential,
        string runnerId,
        string processGeneration)
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/register",
            RegisterBody(processGeneration));
        response.EnsureSuccessStatusCode();
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
