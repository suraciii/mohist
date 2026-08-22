namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// In-process entry points of the event dispatch engine. Workers claim and
/// drain one stream at a time; explicit drains run the same loop until no
/// stream is claimable (tests, operator tooling).
/// </summary>
public interface IEventDispatcher
{
    /// <summary>
    /// Claims and drains one stream. Returns false when nothing is
    /// claimable right now — every pending stream is leased, parked, or
    /// absent.
    /// </summary>
    Task<bool> ClaimAndDrainOneAsync(string owner, CancellationToken ct = default);

    /// <summary>
    /// Runs claim-and-drain passes until no stream is claimable. The
    /// synchronous-drain contract for callers that must observe a completed
    /// drain before proceeding.
    /// </summary>
    Task DrainAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads the dead-letter row and re-dispatches the original event to
    /// the failing handler recorded on that row — the operator recovery
    /// path for poison messages whose retries exhausted.
    /// </summary>
    Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default);
}

public sealed record DeadLetterRedeliveryResult(
    bool Found,
    bool Delivered,
    int Attempts,
    string? Error);
