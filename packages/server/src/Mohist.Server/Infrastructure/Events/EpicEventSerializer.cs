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
    private static readonly IReadOnlyDictionary<Type, string> BusTypes = new Dictionary<Type, string>
    {
        [typeof(EpicCreated)] = EventCatalog.ReverseDns.EpicCreated,
        [typeof(EpicUpdated)] = EventCatalog.ReverseDns.EpicUpdated,
        [typeof(EpicPriorityChanged)] = EventCatalog.ReverseDns.EpicPriorityChanged,
        [typeof(EpicStatusChanged)] = EventCatalog.ReverseDns.EpicStatusChanged,
        [typeof(EpicClosed)] = EventCatalog.ReverseDns.EpicClosed,
        [typeof(EpicReopened)] = EventCatalog.ReverseDns.EpicReopened,
        [typeof(EpicStartAttemptFailed)] = EventCatalog.ReverseDns.EpicStartAttemptFailed,
    };

    internal static IReadOnlyCollection<string> ProducedTypes => BusTypes.Values.ToArray();

    /// <summary>
    /// Storage-facing type: the variant's CLR type name (matches the
    /// <c>Type</c> persisted in <c>EpicEvents</c>).
    /// </summary>
    public static string Type(EpicEvent payload) => Unwrap(payload).GetType().Name;

    /// <summary>
    /// CloudEvents 1.0.2 reverse-DNS <c>type</c> string for the bus.
    /// </summary>
    public static string BusType(EpicEvent payload)
    {
        var variant = Unwrap(payload);
        return BusTypes.TryGetValue(variant.GetType(), out var type)
            ? type
            : throw new InvalidOperationException($"No CloudEvents type for {variant.GetType().Name}");
    }

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
        EpicStatusChanged x => x,
        EpicClosed x => x,
        EpicReopened x => x,
        EpicStartAttemptFailed x => x,
        null => throw new ArgumentNullException(nameof(payload)),
    };
}
