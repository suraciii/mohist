using Mohist.Server.Auth.Domain;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// The runtime identity resolved for an authenticated request: the
/// Principal it belongs to plus the scopes the presented credential
/// carries.
/// </summary>
public sealed record MohistPrincipal(string Id, PrincipalKind Kind, string Name, IReadOnlyList<Scope> Scopes)
{
    public const string AdminPrincipalId = "admin";
    public const string ServicePrincipalId = "service";
    public const string HttpContextItemKey = "mohist.principal";
}
