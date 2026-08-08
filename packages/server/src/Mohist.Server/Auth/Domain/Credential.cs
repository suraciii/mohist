namespace Mohist.Server.Auth.Domain;

public sealed record Credential(
    string Id,
    string PrincipalId,
    CredentialKind Kind,
    string TokenHash,
    IReadOnlyList<Scope> Scopes,
    string? Name,
    string? Prefix,
    string? ProjectId,
    string? FamilyId,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt);
