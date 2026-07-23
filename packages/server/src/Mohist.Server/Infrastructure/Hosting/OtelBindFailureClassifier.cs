using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Otel;

namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Classifies host-start exceptions as OTLP bind failures by matching the
/// exception against the configured collector
/// <see cref="OtelListenerIntent"/>. The outbound trace exporter's
/// <c>Endpoint</c> is intentionally never consulted: a different exporter
/// URI must not change bind-failure classification.
/// </summary>
public sealed class OtelBindFailureClassifier : IOtelBindFailureClassifier
{
    private readonly ILogger<OtelBindFailureClassifier>? _logger;

    public OtelBindFailureClassifier(ILogger<OtelBindFailureClassifier>? logger = null)
    {
        _logger = logger;
    }

    public CollectorBindFailureDecision Classify(Exception exception, MohistHostPlan plan)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.ListenerIntent is not { } listener)
            return new CollectorBindFailureDecision(null);

        if (exception is not IOException io)
            return new CollectorBindFailureDecision(null);

        if (!OtelBindFailureDetector.IsOtlpPortBindFailure(io, listener.Port))
            return new CollectorBindFailureDecision(null);

        OtelPortBindingLog.WriteBindFailure(listener.Port, listener.BindHost, io);
        _logger?.LogWarning(
            io,
            "Mohist host fallback triggered after OTLP bind failure on {Host}:{Port}; alternate host will be started.",
            listener.BindHost,
            listener.Port);

        return new CollectorBindFailureDecision(CollectorResult.BindFailed(io.Message));
    }
}
