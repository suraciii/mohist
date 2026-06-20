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

    /// <summary>
    /// Storage-facing type: the variant's CLR type name (matches the
    /// <c>Type</c> persisted in <c>IssueEvents</c>).
    /// </summary>
    public static string Type(IssueEvent payload) => Unwrap(payload).GetType().Name;

    /// <summary>
    /// CloudEvents 1.0.2 reverse-DNS <c>type</c> string for the bus.
    /// </summary>
    public static string BusType(IssueEvent payload) => Unwrap(payload) switch
    {
        IssueCreated => "com.mohist.issue.created",
        IssueLabelsChanged => "com.mohist.issue.labels-changed",
        IssuePriorityChanged => "com.mohist.issue.priority-changed",
        IssueDraftChanged => "com.mohist.issue.draft-changed",
        IssuePrerequisiteAdded => "com.mohist.issue.prerequisite-added",
        IssuePrerequisiteRemoved => "com.mohist.issue.prerequisite-removed",
        IssueWorkStarted => "com.mohist.issue.work-started",
        IssueWorkCompleted => "com.mohist.issue.work-completed",
        IssueClosed => "com.mohist.issue.closed",
        IssueArchived => "com.mohist.issue.archived",
        IssueUnarchived => "com.mohist.issue.unarchived",
        IssueReopened => "com.mohist.issue.reopened",
        _ => throw new InvalidOperationException($"No CloudEvents type for {Unwrap(payload).GetType().Name}"),
    };

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
        IssueWorkStarted x => x,
        IssueWorkCompleted x => x,
        IssueClosed x => x,
        IssueArchived x => x,
        IssueUnarchived x => x,
        IssueReopened x => x,
        null => throw new ArgumentNullException(nameof(payload)),
    };
}
