namespace Mohist.Server.Logging;

public sealed record LogRecord(
    string? Level,
    DateTimeOffset Time,
    string? Service,
    string Message,
    string? Exception = null,
    IReadOnlyList<KeyValuePair<string, object?>>? Fields = null,
    string? Component = null);
