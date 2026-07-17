using System.Text.Json;

namespace Mohist.Server.Infrastructure.Data.Events;

/// <summary>
/// CloudEvents 1.0.2 envelope persisted for each issue domain event.
/// One row per (Source, Id) where Source identifies the issue
/// (<c>/mohist/projects/{projectId}/issues/{issueNumber}</c>) and Id is the
/// per-source sequence.
/// Mirrors <see cref="WorkflowRunEventRow"/>; the two are kept as
/// separate tables so workflow and issue remain distinct bounded
/// contexts at the storage layer.
/// </summary>
public sealed class IssueEventRow : IEventRow
{
    public required long Id { get; init; }
    public required string Source { get; init; }
    public string TimelineSource { get; init; } = "";
    public required string EventId { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset Time { get; init; }
    public string? TimeSortKey { get; set; }
    public required string SpecVersion { get; init; }
    public string? Subject { get; init; }
    public required string DataContentType { get; init; }
    public required JsonElement Data { get; init; }
    public required string ExtensionsJson { get; init; }
    public DateTimeOffset? DispatchedAt { get; set; }
}
