using Mohist.Server.Otel;

namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Owns one host attempt's full lifecycle: the resolved service graph,
/// the asynchronous start signal, and the post-start wait for shutdown.
/// The production adapter wraps a <c>WebApplication</c>; tests use
/// signal-controlled fakes.
/// </summary>
public interface IMohistHost
{
    IServiceProvider Services { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task WaitForShutdownAsync(CancellationToken cancellationToken);

    ValueTask DisposeAsync();
}

/// <summary>
/// Immutable inputs the production factory uses to construct either the
/// primary or the alternate host through one shared build path.
/// </summary>
/// <remarks>
/// The plan captures the process-level runtime epoch, the configured
/// OTel master switch, the listener intent bound to Kestrel, and the
/// initial collector publication. Both the primary and the alternate
/// factories receive the same epoch; the alternate only differs by the
/// omitted listener intent and the replaced initial
/// <see cref="CollectorResult"/>.
/// </remarks>
public sealed record MohistHostPlan
{
    public MohistHostPlan(
        RuntimeEpoch epoch,
        bool enabled,
        OtelListenerIntent? listenerIntent,
        CollectorResult initialCollectorResult)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentNullException.ThrowIfNull(initialCollectorResult);
        Epoch = epoch;
        Enabled = enabled;
        ListenerIntent = listenerIntent;
        InitialCollectorResult = initialCollectorResult;
    }

    public RuntimeEpoch Epoch { get; }
    public bool Enabled { get; }
    public OtelListenerIntent? ListenerIntent { get; }
    public CollectorResult InitialCollectorResult { get; }

    /// <summary>
    /// Builds a primary plan: OTel disabled, or enabled without an inbound
    /// collector listener, uses no listener intent; enabled plans with a
    /// positive collector port pair that listener intent with an unverified
    /// collector that will be promoted to online after a successful
    /// <c>StartAsync</c>.
    /// </summary>
    public static MohistHostPlan Primary(
        RuntimeEpoch epoch,
        bool enabled,
        OtelListenerIntent? listenerIntent)
    {
        return new(epoch, enabled, listenerIntent, CollectorResult.Unverified());
    }

    /// <summary>
    /// Builds an alternate plan that mirrors the primary on epoch and
    /// enabled intent, omits the OTLP listener intent, and seeds the
    /// collector as <see cref="CollectorResult.BindFailed"/> so the
    /// alternate's initial snapshot exposes that code as
    /// <c>latest_degradation</c>.
    /// </summary>
    public static MohistHostPlan Alternate(MohistHostPlan primary) =>
        new(
            primary.Epoch,
            primary.Enabled,
            listenerIntent: null,
            initialCollectorResult: CollectorResult.BindFailed());
}

/// <summary>
/// Collector bind intent expressed independently from the outbound trace
/// exporter's <c>Endpoint</c>. The bind-failure classifier consumes this
/// intent; it never sees the exporter URI.
/// </summary>
public sealed record OtelListenerIntent(string BindHost, int Port);
