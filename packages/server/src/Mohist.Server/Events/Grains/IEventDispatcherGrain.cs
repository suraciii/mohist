namespace Mohist.Server.Events.Grains;

/// <summary>
/// Cluster-singleton event dispatcher. Activated under a well-known string key
/// (<see cref="EventDispatcherGrain.Global"/>) so Orleans' placement guarantees
/// exactly one active instance per cluster. A persistent reminder drives the
/// <c>~1s</c> dispatch cadence so the loop self-heals across silo crashes
/// without any external signal.
/// </summary>
public interface IEventDispatcherGrain : IGrainWithStringKey, IRemindable
{
    /// <summary>
    /// Best-effort immediate dispatch tick. Producers call this after
    /// committing an event row to nudge latency down; correctness does NOT
    /// depend on it — the persisted reminder alone guarantees at-least-once
    /// delivery within one reminder period. The call is fire-and-forget from
    /// the producer's point of view: any exception is logged by the
    /// dispatcher and swallowed.
    /// </summary>
    Task DispatchNowAsync(CancellationToken ct = default);

    /// <summary>
    /// Latency-shaving poke fired by automatic producers after committing
    /// events. Unlike <see cref="DispatchNowAsync"/>, a poke NEVER queues
    /// behind an in-flight cycle: when a dispatch cycle is already running
    /// it returns immediately, because the reminder cadence alone guarantees
    /// eventual delivery. Callers that must observe a completed drain before
    /// proceeding (tests, operator actions) use <see cref="DispatchNowAsync"/>.
    /// </summary>
    Task PokeAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads the dead-letter row identified by <paramref name="deadLetterId"/>
    /// and re-dispatches the original event to the failing handler recorded
    /// on that row. Used as the manual operator recovery path for poison
    /// messages whose retries exhausted.
    /// </summary>
    Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default);
}

[GenerateSerializer]
public sealed record DeadLetterRedeliveryResult(
    [property: Id(0)] bool Found,
    [property: Id(1)] bool Delivered,
    [property: Id(2)] int Attempts,
    [property: Id(3)] string? Error);
