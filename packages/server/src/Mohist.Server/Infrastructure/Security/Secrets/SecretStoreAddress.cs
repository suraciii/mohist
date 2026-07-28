namespace Mohist.Server.Infrastructure.Security.Secrets;

/// <summary>
/// Identity of a single encrypted secret. A
/// <see cref="SecretStoreAddress"/> is the seam the rest of Server uses to
/// reference a secret — never an internal row id or a raw connection id
/// alone, so a Connection cannot accidentally read another Connection's
/// token. <see cref="ProjectId"/> scopes the address to the Agent
/// project; <see cref="ConnectionId"/> namespaces one Connection's
/// app/bot pair; <see cref="Kind"/> distinguishes which slot is
/// addressed.
/// </summary>
public readonly record struct SecretStoreAddress(
    string ProjectId,
    string ConnectionId,
    SecretKind Kind)
{
    public string ProjectId { get; init; } = ProjectId;
    public string ConnectionId { get; init; } = ConnectionId;
    public SecretKind Kind { get; init; } = Kind;
}
