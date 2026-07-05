namespace Mohist.Server.Logging;

/// <summary>
/// Structured per-line record written to the server log file. This is the
/// wire shape the file logger emits and the tail endpoint projects into
/// <c>LogEntry</c> for the Web client.
/// </summary>
/// <remarks>
/// Field names are lowercase to match the API's <c>JsonNamingPolicy.CamelCase</c>
/// serialization (camelCase) used by the rest of the pipeline; the file
/// logger serializes through <c>JSON.Options</c> so the on-disk format and
/// the API response format agree by construction.
/// </remarks>
public sealed record LogRecord(
    string? Level,
    DateTimeOffset Time,
    string? Service,
    string Message,
    string? Exception = null,
    IReadOnlyDictionary<string, object?>? Fields = null);
