using System.Text.Json;
using Mohist.Server.Epic.Domain.Events;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Maps <see cref="EpicEvent"/> union variants to CloudEvents 1.0.2
/// reverse-DNS <c>type</c> strings. Mirrors <see cref="IssueEventSerializer"/>.
/// </summary>
internal static class EpicEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = JSON.Options;

    /// <summary>
    /// Storage-facing type: the variant's CLR type name (matches the
    /// <c>Type</c> persisted in <c>EpicEvents</c>).
    /// </summary>
    public static string Type(EpicEvent payload) => Unwrap(payload).GetType().Name;

    /// <summary>
    /// CloudEvents 1.0.2 reverse-DNS <c>type</c> string for the bus.
    /// </summary>
    public static string BusType(EpicEvent payload) => Unwrap(payload) switch
    {
        EpicCreated => EventCatalog.ReverseDns.EpicCreated,
        EpicUpdated => EventCatalog.ReverseDns.EpicUpdated,
        EpicPriorityChanged => EventCatalog.ReverseDns.EpicPriorityChanged,
        EpicIssueLinked => EventCatalog.ReverseDns.EpicIssueLinked,
        EpicIssueUnlinked => EventCatalog.ReverseDns.EpicIssueUnlinked,
        EpicStatusChanged => EventCatalog.ReverseDns.EpicStatusChanged,
        EpicClosed => EventCatalog.ReverseDns.EpicClosed,
        EpicReopened => EventCatalog.ReverseDns.EpicReopened,
        EpicStartAttemptFailed => EventCatalog.ReverseDns.EpicStartAttemptFailed,
        _ => throw new InvalidOperationException($"No CloudEvents type for {Unwrap(payload).GetType().Name}"),
    };

    public static JsonElement ToData(EpicEvent payload) =>
        JsonSerializer.SerializeToElement(Unwrap(payload), JsonOptions);

    /// <summary>
    /// Unwrap the C# 14 union to its concrete case for reflection / switch.
    /// </summary>
    public static object Unwrap(EpicEvent payload) => payload switch
    {
        EpicCreated x => x,
        EpicUpdated x => x,
        EpicPriorityChanged x => x,
        EpicIssueLinked x => x,
        EpicIssueUnlinked x => x,
        EpicStatusChanged x => x,
        EpicClosed x => x,
        EpicReopened x => x,
        EpicStartAttemptFailed x => x,
        null => throw new ArgumentNullException(nameof(payload)),
    };
}