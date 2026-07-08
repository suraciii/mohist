using System.Text.Json;

namespace Mohist.Server.Infrastructure.Events;

public interface IDeadLetterStore
{
    Task WriteAsync(DeadLetterRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<DeadLetterRecord>> ListAsync(int limit = 100, CancellationToken ct = default);
}

public sealed record DeadLetterRecord(
    EventOrigin Origin,
    long Id,
    string Source,
    string EventId,
    string Type,
    DateTimeOffset Time,
    string SpecVersion,
    string? Subject,
    string DataContentType,
    JsonElement Data,
    string ExtensionsJson,
    string FailingHandler,
    string ErrorMessage,
    string? ErrorStack,
    int AttemptCount,
    DateTimeOffset DeadLetteredAt);
