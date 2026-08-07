namespace Mohist.Server.Auth.Domain;

public static class Scopes
{
    /// <summary>
    /// The scopes a Principal of the given kind may hold. A credential's
    /// scopes must never exceed its Principal's capability; agent
    /// Principals are attribution anchors and never hold credentials.
    /// </summary>
    public static IReadOnlyList<Scope> CapabilityOf(PrincipalKind kind) =>
        kind switch
        {
            PrincipalKind.Admin or PrincipalKind.Service => [Scope.Operator],
            _ => [],
        };

    public static bool IsWithinCapability(IEnumerable<Scope> scopes, PrincipalKind kind)
    {
        var capability = CapabilityOf(kind);
        return scopes.All(capability.Contains);
    }
}
