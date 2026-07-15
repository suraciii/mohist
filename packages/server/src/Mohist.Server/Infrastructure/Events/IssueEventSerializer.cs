using System.Text.Json;
using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Maps <see cref="IssueEvent"/> union variants to CloudEvents 1.0.2
/// reverse-DNS <c>type</c> strings. Mirrors <see cref="WorkflowEventSerializer"/>.
/// </summary>
internal static class IssueEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = JSON.Options;
    private static readonly IReadOnlyDictionary<Type, string> BusTypes = new Dictionary<Type, string>
    {
        [typeof(IssueCreated)] = EventCatalog.ReverseDns.IssueCreated,
        [typeof(IssueLabelsChanged)] = EventCatalog.ReverseDns.IssueLabelsChanged,
        [typeof(IssuePriorityChanged)] = EventCatalog.ReverseDns.IssuePriorityChanged,
        [typeof(IssueDraftChanged)] = EventCatalog.ReverseDns.IssueDraftChanged,
        [typeof(IssuePrerequisiteAdded)] = EventCatalog.ReverseDns.IssuePrerequisiteAdded,
        [typeof(IssuePrerequisiteRemoved)] = EventCatalog.ReverseDns.IssuePrerequisiteRemoved,
        [typeof(IssueWorkflowProfileChanged)] = EventCatalog.ReverseDns.IssueWorkflowProfileChanged,
        [typeof(IssueWorkStarted)] = EventCatalog.ReverseDns.IssueWorkStarted,
        [typeof(IssueCompleted)] = EventCatalog.ReverseDns.IssueCompleted,
        [typeof(IssueCancelled)] = EventCatalog.ReverseDns.IssueCancelled,
        [typeof(IssueArchived)] = EventCatalog.ReverseDns.IssueArchived,
        [typeof(IssueUnarchived)] = EventCatalog.ReverseDns.IssueUnarchived,
        [typeof(IssueReopened)] = EventCatalog.ReverseDns.IssueReopened,
    };

    internal static IReadOnlyCollection<string> ProducedTypes => BusTypes.Values.ToArray();

    /// <summary>
    /// Storage-facing type: the variant's CLR type name (matches the
    /// <c>Type</c> persisted in <c>IssueEvents</c>).
    /// </summary>
    public static string Type(IssueEvent payload) => Unwrap(payload).GetType().Name;

    /// <summary>
    /// CloudEvents 1.0.2 reverse-DNS <c>type</c> string for the bus.
    /// </summary>
    public static string BusType(IssueEvent payload)
    {
        var variant = Unwrap(payload);
        return BusTypes.TryGetValue(variant.GetType(), out var type)
            ? type
            : throw new InvalidOperationException($"No CloudEvents type for {variant.GetType().Name}");
    }

    public static JsonElement ToData(IssueEvent payload) =>
        JsonSerializer.SerializeToElement(Unwrap(payload), JsonOptions);

    /// <summary>
    /// Unwrap the C# 14 union to its concrete case for reflection / switch.
    /// </summary>
    public static object Unwrap(IssueEvent payload) => payload switch
    {
        IssueCreated x => x,
        IssueLabelsChanged x => x,
        IssuePriorityChanged x => x,
        IssueDraftChanged x => x,
        IssuePrerequisiteAdded x => x,
        IssuePrerequisiteRemoved x => x,
        IssueWorkflowProfileChanged x => x,
        IssueWorkStarted x => x,
        IssueCompleted x => x,
        IssueCancelled x => x,
        IssueArchived x => x,
        IssueUnarchived x => x,
        IssueReopened x => x,
        null => throw new ArgumentNullException(nameof(payload)),
    };
}
