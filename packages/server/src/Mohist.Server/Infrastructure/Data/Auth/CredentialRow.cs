namespace Mohist.Server.Infrastructure.Data.Auth;

public class CredentialRow
{
    public string Id { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";
    public string? Name { get; set; }
    public string? Prefix { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
