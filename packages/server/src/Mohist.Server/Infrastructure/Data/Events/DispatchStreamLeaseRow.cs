namespace Mohist.Server.Infrastructure.Data.Events;

/// <summary>
/// Dispatch worker coordination state for one event stream. A row exists
/// only while a worker holds or parks the stream; idle streams have no
/// row. The lease gates who drains, never what is durable: expiry costs
/// at-least-once redelivery of undispatched rows, nothing else.
/// </summary>
public sealed class DispatchStreamLeaseRow
{
    public string Origin { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string LeaseOwner { get; set; } = string.Empty;

    public DateTimeOffset LeaseUntil { get; set; }

    /// <summary>Retry attempts for the stream's current head event; reset
    /// when the head settles.</summary>
    public int Attempts { get; set; }

    /// <summary>When set to the future, the stream is parked in backoff and
    /// not claimable until the timestamp passes.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
