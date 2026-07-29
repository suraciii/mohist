using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.Infrastructure.Data.Secrets;

/// <summary>
/// EF row for one encrypted connection-bound secret. Primary key is the
/// composite <c>(ProjectId, ConnectionId, Kind)</c>; <see cref="Kind"/>
/// is stored as the wire string ("appToken"/"botToken") so the value is
/// readable in <c>sqlite3</c> without an EF layer. <see cref="Blob"/> is
/// the AES-GCM ciphertext concatenated with its nonce and tag.
/// </summary>
public sealed class ConnectionSecretRow
{
    public string ProjectId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public byte[] Blob { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }

    public static string WireKind(SecretKind kind) => SecretKinds.ToWire(kind);

    public static bool TryReadKind(string value, out SecretKind kind) =>
        SecretKinds.TryParseWire(value, out kind);
}
