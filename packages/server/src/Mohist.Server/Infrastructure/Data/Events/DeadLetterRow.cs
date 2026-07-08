using System.Text.Json;

namespace Mohist.Server.Infrastructure.Data.Events;

public sealed class DeadLetterRow
{
    public long DeadLetterId { get; init; }
    public required string Origin { get; init; }
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
    public required string FailingHandler { get; init; }
    public required string ErrorMessage { get; init; }
    public string? ErrorStack { get; init; }
    public required int AttemptCount { get; init; }
    public required DateTimeOffset DeadLetteredAt { get; init; }
}
