using System.Text.Json;

namespace Mohist.Server.Infrastructure.Data.Events;

public sealed class WorkspaceEventRow : IEventRow
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
