namespace Mohist.Server.Infrastructure.Data.Auth;

/// <summary>
/// Attribution anchor row for an Agent principal. Only agent principals
/// are persisted: admin and service are implied by the file-credential
/// bootstrap and never appear here. Rows are never deleted or revoked —
/// historical attribution records point at them permanently.
/// </summary>
public class PrincipalRow
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
