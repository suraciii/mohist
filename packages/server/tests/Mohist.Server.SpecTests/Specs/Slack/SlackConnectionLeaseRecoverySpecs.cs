using System.Text;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed partial class SlackConnectionLeaseTargetSpecs
{
    [Fact]
    public async Task Hello_that_crashes_during_promote_stays_awaiting_socket_and_is_retriable()
    {
        var seeded = await SeedAsync(SlackRuntimeCredentialValidationState.Candidate, bound: false);
        var targetRef = seeded.TargetRef;
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");

        // Crash exactly where the fixed order promotes the candidate to the
        // runtime pair: the first runtime StoreAsync inside promotion. Verified
        // is set only after promotion, so the crash lands before the state
        // leaves AwaitingSocket.
        _secrets.FaultStore(address =>
            address == SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.BotToken));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _leases.ReportHelloAsync("operator-1", targetRef, validation!.LeaseId, seeded.AppId));

        // The crash happened before Verified: the Agent App is still
        // AwaitingSocket, so a runtime lease can never serve the un-promoted
        // candidate.
        await AssertAgentAppStateAsync(seeded.AgentAppId,
            SlackRuntimeCredentialValidationState.AwaitingSocket, SlackAgentAppBindingState.Pending);
        Assert.Null(await _leases.AcquireRuntimeLeaseAsync("operator-1", targetRef, "adapter-A"));

        // Recovery: clear the fault, re-acquire the validation lease (still an
        // unverified candidate) and replay the hello; promotion completes, the
        // state advances to Verified and the binding reconciles.
        _secrets.ClearFault();
        var retryLease = await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");
        Assert.NotNull(retryLease);

        var outcome = await _leases.ReportHelloAsync("operator-1", targetRef, retryLease!.LeaseId, seeded.AppId);
        Assert.Equal(SlackHelloOutcome.Verified, outcome);
        await AssertAgentAppStateAsync(seeded.AgentAppId,
            SlackRuntimeCredentialValidationState.Verified, SlackAgentAppBindingState.Bound);
        await AssertConnectionIdentityAsync(seeded.ConnectionId, seeded.AppId);
    }

    [Fact]
    public async Task Crash_after_candidate_deletion_stays_awaiting_socket_and_resupply_recovers()
    {
        var seeded = await SeedAsync(SlackRuntimeCredentialValidationState.Candidate, bound: false);
        var targetRef = seeded.TargetRef;
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");

        // Crash inside candidate cleanup after promotion: the candidate App
        // token is already deleted when the fault lands, so the candidate slot
        // can no longer back a validation lease.
        _secrets.FaultDelete(address =>
            address == SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.CandidateBotToken));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _leases.ReportHelloAsync("operator-1", targetRef, validation!.LeaseId, seeded.AppId));

        await AssertAgentAppStateAsync(seeded.AgentAppId,
            SlackRuntimeCredentialValidationState.AwaitingSocket, SlackAgentAppBindingState.Pending);
        Assert.Null(await _leases.AcquireRuntimeLeaseAsync("operator-1", targetRef, "adapter-A"));

        // The candidate slot is unusable: re-acquire fails cleanly and leaves
        // no inert active lease behind.
        _secrets.ClearFault();
        Assert.Null(await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A"));
        Assert.Null(await new SlackAdapterLeaseStore(_factory).GetActiveAsync(targetRef.TargetKey));

        // Resupply restages the candidate while the state stays AwaitingSocket;
        // the replayed hello then converges to Verified and bound.
        await _secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.CandidateAppToken),
            Encoding.UTF8.GetBytes("xapp-candidate"));
        await _secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.CandidateBotToken),
            Encoding.UTF8.GetBytes("xoxb-candidate"));

        var retryLease = await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");
        Assert.NotNull(retryLease);
        var outcome = await _leases.ReportHelloAsync("operator-1", targetRef, retryLease!.LeaseId, seeded.AppId);
        Assert.Equal(SlackHelloOutcome.Verified, outcome);
        await AssertAgentAppStateAsync(seeded.AgentAppId,
            SlackRuntimeCredentialValidationState.Verified, SlackAgentAppBindingState.Bound);
        await AssertConnectionIdentityAsync(seeded.ConnectionId, seeded.AppId);
    }

    private sealed class FaultingSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        private Func<SecretStoreAddress, bool>? _storeFault;
        private Func<SecretStoreAddress, bool>? _deleteFault;

        public void FaultStore(Func<SecretStoreAddress, bool> predicate) => _storeFault = predicate;
        public void FaultDelete(Func<SecretStoreAddress, bool> predicate) => _deleteFault = predicate;
        public void ClearFault()
        {
            _storeFault = null;
            _deleteFault = null;
        }

        public async Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            if (_storeFault is { } fault && fault(address))
                throw new InvalidOperationException("Injected promote fault.");
            await _inner.StoreAsync(address, plaintext, ct);
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            _inner.LoadAsync(address, ct);

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default)
        {
            if (_deleteFault is { } fault && fault(address))
                throw new InvalidOperationException("Injected delete fault.");
            return _inner.DeleteAsync(address, ct);
        }

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) =>
            _inner.Redact(values);
    }
}
