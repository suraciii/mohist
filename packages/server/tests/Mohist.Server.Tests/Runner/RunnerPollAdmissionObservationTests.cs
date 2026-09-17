using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Xunit;

namespace Mohist.Server.Tests.Runner;

[Trait("level", "L0")]
public sealed class RunnerPollAdmissionObservationTests
{
    [Fact]
    public void CurrentLease_NormalizesReadyObservationWithoutBlockers()
    {
        var tracker = new RunnerConnectionTracker();
        const string runnerId = "runner-1";
        const string connectionId = "connection-1";
        var generation = tracker.Register(runnerId, connectionId);

        var normalized = tracker.ApplyPollAdmission(
            runnerId,
            new RunnerPollRequest(
                [],
                [],
                ConnectionId: connectionId,
                AdmissionReady: true,
                AdmissionReasonCodes: []));

        Assert.Equal(generation, normalized.ConnectionGeneration);
        Assert.True(normalized.AdmissionReady);
        Assert.Empty(normalized.AdmissionReasonCodes!);
    }

    [Fact]
    public void CurrentLease_PreservesOrderedStableBlockers()
    {
        var tracker = new RunnerConnectionTracker();
        const string runnerId = "runner-1";
        const string connectionId = "connection-1";
        tracker.Register(runnerId, connectionId);

        var normalized = tracker.ApplyPollAdmission(
            runnerId,
            new RunnerPollRequest(
                [],
                [],
                ConnectionId: connectionId,
                AdmissionReady: false,
                AdmissionReasonCodes:
                [
                    RunnerAdmissionReasonCodes.RuntimeEventQueueUnavailable,
                    RunnerAdmissionReasonCodes.ProviderPolicyInvalid,
                ]));

        Assert.False(normalized.AdmissionReady);
        Assert.Equal(
            [
                RunnerAdmissionReasonCodes.ProviderPolicyInvalid,
                RunnerAdmissionReasonCodes.RuntimeEventQueueUnavailable,
            ],
            normalized.AdmissionReasonCodes);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(true, "unknown-blocker")]
    [InlineData(false, null)]
    [InlineData(true, "provider-policy-invalid")]
    public void MissingOrMalformedObservation_FailsClosed(
        bool? admissionReady,
        string? reasonCode)
    {
        var tracker = new RunnerConnectionTracker();
        const string runnerId = "runner-1";
        const string connectionId = "connection-1";
        tracker.Register(runnerId, connectionId);

        var normalized = tracker.ApplyPollAdmission(
            runnerId,
            new RunnerPollRequest(
                [],
                [],
                ConnectionId: connectionId,
                AdmissionReady: admissionReady,
                AdmissionReasonCodes: reasonCode is null ? [] : [reasonCode]));

        Assert.False(normalized.AdmissionReady);
        Assert.Equal(
            [RunnerAdmissionReasonCodes.ObservationInvalid],
            normalized.AdmissionReasonCodes);
    }

    [Fact]
    public void OldConnectionObservation_FailsClosedAndDoesNotBorrowCurrentGeneration()
    {
        var tracker = new RunnerConnectionTracker();
        const string runnerId = "runner-1";
        var oldGeneration = tracker.Register(runnerId, "old-connection");
        var currentGeneration = tracker.Register(runnerId, "current-connection");

        var normalized = tracker.ApplyPollAdmission(
            runnerId,
            new RunnerPollRequest(
                [],
                [],
                ConnectionId: "old-connection",
                AdmissionReady: true,
                AdmissionReasonCodes: []));

        Assert.NotEqual(oldGeneration, currentGeneration);
        Assert.Null(normalized.ConnectionGeneration);
        Assert.False(normalized.AdmissionReady);
        Assert.Equal(
            [RunnerAdmissionReasonCodes.ObservationInvalid],
            normalized.AdmissionReasonCodes);
    }
}
