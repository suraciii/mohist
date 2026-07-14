using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Mohist.Server.Infrastructure.Data.Events;

/// <summary>
/// CloudEvents 1.0.2 envelope persisted for each workflow domain event.
/// One row per (Source, Id) where Source identifies the workflow run and Id
/// is the per-source sequence. The envelope's own attributes (id, source,
/// type, time, subject, data, extensions, specversion) are stored as
/// columns so the row is self-describing without the dispatch layer.
/// </summary>
public sealed class WorkflowRunEventRow : IEventRow
{
    public required long Id { get; init; }
    public required string Source { get; init; }
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
