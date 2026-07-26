using System.Text.Json;

namespace Mohist.Server.Infrastructure.Data.Events;

/// <summary>
/// CloudEvents 1.0.2 envelope persisted for each AgentJob domain event.
/// One row per (Source, Id) where Source identifies the AgentJob
/// (<c>/mohist/agent-job/{id}</c>) and Id is the per-source sequence.
/// Mirrors <see cref="AgentSessionEventRow"/>; the two are kept as
/// separate tables so agent jobs and agent sessions remain distinct
/// bounded contexts at the storage layer.
/// </summary>
public sealed class AgentJobEventRow : IEventRow
{
    public required long Id { get; init; }
    public required string Source { get; init; }
    public required string EventId { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset Time { get; init; }
    public string? TimeSortKey { get; set; }
    public string? DataStatus { get; set; }
    public required string SpecVersion { get; init; }
    public string? Subject { get; init; }
    public required string DataContentType { get; init; }
    public required JsonElement Data { get; init; }
    public required string ExtensionsJson { get; init; }
    public DateTimeOffset? DispatchedAt { get; set; }
}
