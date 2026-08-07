using System.Text.Json.Serialization;

namespace Mohist.Server.Logging;

/// <summary>
/// Per-line element type for the <c>/api/logs/tail</c> response. This is
/// the agreed shape between the server and the Web client: the server
/// emits each element already in this structured form, the client renders
/// it without re-parsing.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Raw"/> is the faithful original logfmt line; search and export
/// operate on it even when structured fields are absent.
/// </para>
/// <para>
/// Every property carries <c>[JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c>
/// so the shared <c>JSON.Options</c> setting of <c>DefaultIgnoreCondition = WhenWritingNull</c>
/// does not strip a null <c>level</c>/<c>time</c>/<c>service</c> off the wire
/// payload — the spec requires the field be present (as <c>null</c>) so the
/// client never sees <c>undefined</c>.
/// </para>
/// </remarks>
public sealed record LogEntry(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Level,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Time,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Service,
    string Message,
    string Raw);

/// <summary>
/// The full <c>/api/logs/tail</c> response. Every field is always
/// present; the discriminator between an available source with zero new
/// lines and a missing source is <see cref="Unavailable"/>.
/// </summary>
/// <remarks>
/// A single typed shape (not a tagged union) is the agreed
/// contract. <see cref="Cursor"/> and <see cref="NextCursor"/> are byte
/// offsets into the active log file; <see cref="Source"/> is the active
/// file name; <see cref="Reset"/> tells the client to replace its view;
/// <see cref="Truncated"/> tells the client the read was bounded before
/// EOF; when <see cref="Unavailable"/> is true,
/// <see cref="ExpectedLocation"/> and <see cref="Reason"/> explain why.
/// <para>
/// Every nullable property carries <c>[JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c>
/// so the global <c>DefaultIgnoreCondition = WhenWritingNull</c> setting
/// does not strip a missing field off the wire payload — the spec requires
/// all of them to be present (as <c>null</c> when absent) so the client
/// never sees <c>undefined</c>.
/// </para>
/// </remarks>
public sealed record LogTailResponse(
    IReadOnlyList<LogEntry> Lines,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Cursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? NextCursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Source,
    bool Truncated,
    bool Reset,
    bool Unavailable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ExpectedLocation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason);
