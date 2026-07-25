using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;

namespace Mohist.Server.UnitTests.HostLifecycle;

/// <summary>
/// Records classification decisions and returns the configured
/// <see cref="CollectorResult"/>. Tests use this to verify the
/// listener-intent-only behavior of the runner's classifier callback.
/// </summary>
public sealed class FakeOtelBindFailureClassifier : IOtelBindFailureClassifier
{
    private readonly List<ClassificationCall> _calls = new();

    public IReadOnlyList<ClassificationCall> Calls => _calls;

    public Exception? ThrowsOnClassify { get; set; }
    public CollectorResult? Result { get; set; }

    public CollectorBindFailureDecision Classify(Exception exception, MohistHostPlan plan)
    {
        _calls.Add(new ClassificationCall(exception, plan));
        if (ThrowsOnClassify is { } throws)
            throw throws;
        return new CollectorBindFailureDecision(Result);
    }

    public sealed record ClassificationCall(Exception Exception, MohistHostPlan Plan);
}
