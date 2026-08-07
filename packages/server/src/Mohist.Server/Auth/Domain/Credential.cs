namespace Mohist.Server.Auth.Domain;

public sealed record Credential(
    string Id,
    string PrincipalId,
    CredentialKind Kind,
    string TokenHash,
    IReadOnlyList<Scope> Scopes,
    string? Name,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt);
