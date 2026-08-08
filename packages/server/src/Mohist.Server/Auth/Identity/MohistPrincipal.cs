using Mohist.Server.Auth.Domain;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// The runtime identity resolved for an authenticated request: the
/// Principal it belongs to plus the scopes the presented credential
/// carries. <see cref="RunnerId"/> is the runner the credential is
/// bound to (runner-kind credentials only); the auth layer rejects any
/// runner-scoped request whose self-declared runner id does not match
/// it (docs/auth.md Runner 顶替防护).
/// </summary>
public sealed record MohistPrincipal(
    string Id,
    PrincipalKind Kind,
    string Name,
    IReadOnlyList<Scope> Scopes,
    string? RunnerId = null)
{
    public const string AdminPrincipalId = "admin";
    public const string ServicePrincipalId = "service";
    public const string HttpContextItemKey = "mohist.principal";
}
