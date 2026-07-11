namespace Mohist.Server.Events.Grains;

/// <summary>
/// Cluster-singleton dispatcher grain. Orleans activates exactly one
/// instance cluster-wide under the fixed key
/// <see cref="FixedKey"/>, making this grain the sole notifier of
/// subscribers. Self-wakes on a persisted reminder
/// (<see cref="ReminderName"/>) and exposes <see cref="PulseAsync"/>
/// as a best-effort latency optimization for producers.
///
/// All actual pull–fan-out–mark work lives in
/// <see cref="Mohist.Server.Infrastructure.Events.EventDispatcherService"/>;
/// this grain is a thin shell so the dispatch core stays
/// silo-free and unit-testable with fakes + an injected
/// <see cref="TimeProvider"/>.
/// </summary>
public interface IDispatcherGrain : IGrainWithStringKey, IRemindable
{
    Task EnsureStartedAsync();

    /// <summary>
    /// Best-effort immediate dispatch tick. Producers call this after
    /// committing an event row to nudge latency down (e.g. ~24h → ~1s);
    /// correctness does NOT depend on it — the persisted reminder alone
    /// guarantees at-least-once delivery within one reminder period.
    /// </summary>
    Task PulseAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads the dead-letter row identified by
    /// <paramref name="deadLetterId"/> and re-dispatches the original
    /// event to the failing handler recorded on that row. Used as the
    /// manual operator recovery path for poison messages whose retries
    /// exhausted.
    /// </summary>
    Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default);
}

[GenerateSerializer]
public sealed record DeadLetterRedeliveryResult(
    [property: Id(0)] bool Found,
    [property: Id(1)] bool Delivered,
    [property: Id(2)] int Attempts,
    [property: Id(3)] string? Error);
