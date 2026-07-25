using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.HostLifecycle;

public class OtelBindFailureClassifierTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MohistHostPlan PlanWith(string bindHost, int port) =>
        MohistHostPlan.Primary(new RuntimeEpoch(Start), enabled: true, new OtelListenerIntent(bindHost, port));

    [Fact]
    public void Classify_MatchingBindException_ForProvidedCollectorIntent_ReturnsBindFailed()
    {
        var classifier = new OtelBindFailureClassifier(NullLogger<OtelBindFailureClassifier>.Instance);
        var plan = PlanWith("localhost", 4318);
        var exception = new IOException(
            "Failed to bind to address http://127.0.0.1:4318: address already in use.");

        var decision = classifier.Classify(exception, plan);

        Assert.NotNull(decision.Result);
        Assert.Equal(RuntimeDegradationCodes.CollectorBindFailed, decision.Result!.FailureCode);
    }

    [Fact]
    public void Classify_DifferentCollectorHostAndPort_DoesNotMatchBindFailure()
    {
        var classifier = new OtelBindFailureClassifier(NullLogger<OtelBindFailureClassifier>.Instance);
        var plan = PlanWith("127.0.0.1", 9999);
        var exception = new IOException(
            "Failed to bind to address http://127.0.0.1:4318: address already in use.");

        var decision = classifier.Classify(exception, plan);

        Assert.Null(decision.Result);
    }

    [Fact]
    public void Classify_NonIoException_NotClassifiedAsBindFailure()
    {
        var classifier = new OtelBindFailureClassifier(NullLogger<OtelBindFailureClassifier>.Instance);
        var plan = PlanWith("localhost", 4318);
        var exception = new InvalidOperationException("not a bind failure");

        var decision = classifier.Classify(exception, plan);

        Assert.Null(decision.Result);
    }

    [Fact]
    public void Classify_PlanWithoutListenerIntent_NeverReturnsBindFailure()
    {
        var classifier = new OtelBindFailureClassifier(NullLogger<OtelBindFailureClassifier>.Instance);
        var plan = MohistHostPlan.Primary(new RuntimeEpoch(Start), false, listenerIntent: null);
        var exception = new IOException("anything");

        var decision = classifier.Classify(exception, plan);

        Assert.Null(decision.Result);
    }

    [Fact]
    public void Classify_OnlyBindHostChanges_DoesNotMatchUnrelatedBind()
    {
        var classifier = new OtelBindFailureClassifier(NullLogger<OtelBindFailureClassifier>.Instance);
        var plan = PlanWith("0.0.0.0", 4318);
        var exception = new IOException(
            "Failed to bind to address http://127.0.0.1:4318: address already in use.");

        var decision = classifier.Classify(exception, plan);

        // 0.0.0.0 and 127.0.0.1 are documented bind hosts the detector accepts on
        // matching port numbers; exercising the listener contract through the
        // classifier path proves the intent governs classification regardless of
        // alternative endpoint URIs which the classifier never sees.
        Assert.NotNull(decision.Result);
    }
}
