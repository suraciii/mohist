using System.Text.Json;

namespace Mohist.Server.Infrastructure.Data.Events;

/// <summary>
/// CloudEvents 1.0.2 envelope persisted for each event received through a
/// GitHub connection ingress endpoint. One row per (Source, Id) where Source
/// identifies the connection (<c>/mohist/projects/{projectId}/github-connections/{connectionId}</c>)
/// and Id is the per-source sequence. Kept as a separate table so inbound
/// GitHub traffic stays distinct from workflow/issue/epic domain events at
/// the storage layer.
/// </summary>
public sealed class IngressEventRow : IEventRow
{
    public required long Id { get; init; }
    public required string Source { get; init; }
    public required string EventId { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset Time { get; init; }
    public required string SpecVersion { get; init; }
    public string? Subject { get; init; }
    public required string DataContentType { get; init; }
    public required JsonElement Data { get; init; }
    public required string ExtensionsJson { get; init; }
    public DateTimeOffset? DispatchedAt { get; set; }
}
