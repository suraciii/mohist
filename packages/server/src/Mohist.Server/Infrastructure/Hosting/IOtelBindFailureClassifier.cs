using Mohist.Server.Otel;

namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Classifies host-start exceptions against the configured OTel listener
/// intent, returning the collector failure result when bind failure is
/// the cause and <c>null</c> otherwise. The classifier never receives
/// the outbound trace exporter's <c>Endpoint</c>; only the
/// <see cref="OtelListenerIntent"/> governs the decision.
/// </summary>
public interface IOtelBindFailureClassifier
{
    CollectorBindFailureDecision Classify(Exception exception, MohistHostPlan plan);
}

/// <summary>
/// Result of <see cref="IOtelBindFailureClassifier.Classify"/>. The
/// presence of <see cref="CollectorResult"/> signals bind failure and
/// triggers the runner's fallback host plan; <c>null</c> leaves the
/// host attempt terminal without a fallback.
/// </summary>
public readonly record struct CollectorBindFailureDecision(CollectorResult? Result);
