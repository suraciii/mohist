using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed partial class SlackManagerCorrectnessKernelSpecs
{
    [Theory]
    [InlineData(SlackAgentAppBindingState.Conflict, SlackAgentAppBindingObligationStatus.Conflict)]
    [InlineData(SlackAgentAppBindingState.ConnectionDeleted, SlackAgentAppBindingObligationStatus.ConnectionDeleted)]
    public async Task Binding_diagnostic_retry_resets_both_states_and_converges_to_bound(string bindingState, string obligationStatus)
    {
        var child = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            authorization: SlackAuthorizationState.Authorized,
            appId: "A_BIND_RETRY",
            botUserId: "U_BIND_RETRY",
            bindingState: bindingState,
            runtimeCredentialValidationState: SlackRuntimeCredentialValidationState.Verified);
        await SeedBindingObligationAsync(child, obligationStatus);
        var connections = new AgentConnectionStore(
            _factory,
            new AgentQuerier(_factory),
            new FakeSecretStore(),
            Array.Empty<IAgentConnectionProviderCleanup>(),
            _time);
        var binding = new SlackAgentAppBindingService(_factory, connections, _time);

        var result = await binding.ReconcileAsync(child.Id);

        Assert.Equal(SlackAgentAppBindingStatus.Bound, result.Status);
        Assert.Equal(SlackAgentAppBindingState.Bound, await BindingStateAsync(child.Id));
        await using var db = _factory.CreateDbContext();
        var obligation = await db.SlackAgentAppBindingObligations.SingleAsync(item => item.AgentAppId == child.Id);
        Assert.Equal(SlackAgentAppBindingObligationStatus.Bound, obligation.Status);
        Assert.Equal(("A_BIND_RETRY", "U_BIND_RETRY"), await ConnectionIdentityAsync(child.AgentConnectionId));
    }

    [Fact]
    public async Task Expired_worker_failure_cannot_overwrite_newer_success()
    {
        var child = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            authorization: SlackAuthorizationState.Authorized,
            appId: "A_BIND_LEASE_FAILURE",
            botUserId: "U_BIND_LEASE_FAILURE",
            runtimeCredentialValidationState: SlackRuntimeCredentialValidationState.Verified);
        var actual = CreateBindingPortStore();
        var port = new InterleavingBindingPort(actual)
        {
            PauseFirstCall = true,
            FailFirstCall = true,
        };
        var first = new SlackAgentAppBindingService(_factory, port, _time);
        var second = new SlackAgentAppBindingService(_factory, port, _time);

        var staleFailure = first.ReconcileAsync(child.Id);
        await port.FirstCallStarted;
        _time.Advance(TimeSpan.FromMinutes(5));

        var newerSuccess = await second.ReconcileAsync(child.Id);
        Assert.Equal(SlackAgentAppBindingStatus.Bound, newerSuccess.Status);

        port.ReleaseFirstCall();
        var staleResult = await staleFailure;
        Assert.Equal(SlackAgentAppBindingStatus.Bound, staleResult.Status);
        await AssertBindingConvergedAsync(child, SlackAgentAppBindingState.Bound);
    }

    [Fact]
    public async Task Expired_worker_success_cannot_overwrite_newer_failure()
    {
        var child = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            authorization: SlackAuthorizationState.Authorized,
            appId: "A_BIND_LEASE_SUCCESS",
            botUserId: "U_BIND_LEASE_SUCCESS",
            runtimeCredentialValidationState: SlackRuntimeCredentialValidationState.Verified);
        var actual = CreateBindingPortStore();
        var port = new InterleavingBindingPort(actual)
        {
            PauseFirstCall = true,
            FailSecondCall = true,
        };
        var staleWorker = new SlackAgentAppBindingService(_factory, port, _time);
        var newerWorker = new SlackAgentAppBindingService(_factory, port, _time);

        var staleSuccess = staleWorker.ReconcileAsync(child.Id);
        await port.FirstCallStarted;
        _time.Advance(TimeSpan.FromMinutes(5));

        var newerFailure = await newerWorker.ReconcileAsync(child.Id);
        Assert.Equal(SlackAgentAppBindingStatus.Conflict, newerFailure.Status);

        port.ReleaseFirstCall();
        var staleResult = await staleSuccess;
        Assert.Equal(SlackAgentAppBindingStatus.Conflict, staleResult.Status);
        await AssertBindingConvergedAsync(child, SlackAgentAppBindingState.Conflict);
        Assert.Equal((string.Empty, string.Empty), await ConnectionIdentityAsync(child.AgentConnectionId));
    }

    private AgentConnectionStore CreateBindingPortStore() => new(
        _factory,
        new AgentQuerier(_factory),
        new FakeSecretStore(),
        Array.Empty<IAgentConnectionProviderCleanup>(),
        _time);

    private async Task AssertBindingConvergedAsync(ManagedSlackAgentAppRow child, string state)
    {
        Assert.Equal(state, await BindingStateAsync(child.Id));
        await using var db = _factory.CreateDbContext();
        Assert.Equal(state, await db.SlackAgentAppBindingObligations
            .Where(item => item.AgentAppId == child.Id)
            .Select(item => item.Status)
            .SingleAsync());
    }

    private sealed class InterleavingBindingPort(AgentConnectionStore inner) : ISlackAgentAppBindingPort
    {
        private readonly TaskCompletionSource<bool> _firstCallStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFirstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public bool PauseFirstCall { get; init; }
        public bool FailFirstCall { get; init; }
        public bool FailSecondCall { get; init; }
        public Task FirstCallStarted => _firstCallStarted.Task;

        public void ReleaseFirstCall() => _releaseFirstCall.TrySetResult(true);

        public async Task<AgentConnection?> BindSlackIdentityAsync(
            string projectId,
            string id,
            string workspaceTeamId,
            string appId,
            string botUserId,
            string? botName,
            CancellationToken ct = default,
            string? claimToken = null)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                _firstCallStarted.TrySetResult(true);
                if (PauseFirstCall)
                    await _releaseFirstCall.Task.WaitAsync(ct);
                if (FailFirstCall)
                    throw new AgentConnectionDuplicateException(projectId, "agent", workspaceTeamId);
            }
            else if (call == 2 && FailSecondCall)
            {
                throw new AgentConnectionDuplicateException(projectId, "agent", workspaceTeamId);
            }

            return await inner.BindSlackIdentityAsync(projectId, id, workspaceTeamId, appId, botUserId, botName, ct, claimToken);
        }
    }

    [Fact]
    public void Domain_transitions_and_derivation_reject_unknown_or_illegal_states()
    {
        var child = new ManagedSlackAgentApp
        {
            AppLifecycle = SlackAppLifecycle.Created,
            Authorization = SlackAuthorizationState.Authorized,
            BindingState = SlackAgentAppBindingState.Bound,
        };
        Assert.Throws<InvalidOperationException>(() => child.TransitionAppLifecycle(SlackAppLifecycle.NotCreated));
        child.AppLifecycle = "invalid";
        Assert.Throws<ArgumentException>(() => ManagedSlackAgentAppStatusDeriver.Derive(child));

        var enrollment = new SlackWorkspaceEnrollment { Lifecycle = SlackEnrollmentLifecycle.Active };
        Assert.Throws<ArgumentOutOfRangeException>(() => enrollment.UpdatePlan("pro", -1));
    }

    [Fact]
    public async Task Database_constraints_reject_invalid_child_app_lifecycle_and_manifest_pair()
    {
        var child = await SeedChildAsync(lifecycle: SlackAppLifecycle.Created, appId: "A_CHECKS", botUserId: "U_CHECKS");
        await using var invalidLifecycle = _factory.CreateDbContext();
        var row = await invalidLifecycle.ManagedSlackAgentApps.SingleAsync(item => item.Id == child.Id);
        row.AppLifecycle = "invalid";
        await Assert.ThrowsAsync<DbUpdateException>(() => invalidLifecycle.SaveChangesAsync());

        await using var invalidManifest = _factory.CreateDbContext();
        var manifestRow = await invalidManifest.ManagedSlackAgentApps.SingleAsync(item => item.Id == child.Id);
        manifestRow.AppLifecycle = SlackAppLifecycle.Created;
        manifestRow.DesiredManifestVersion = 0;
        await Assert.ThrowsAsync<DbUpdateException>(() => invalidManifest.SaveChangesAsync());
    }
}
