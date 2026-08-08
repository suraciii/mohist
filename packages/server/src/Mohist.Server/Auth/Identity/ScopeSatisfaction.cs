using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// The route-scope satisfaction rule (docs/auth.md scope table): a
/// credential satisfies a route when it satisfies any of the route's
/// declared scopes, and <c>operator</c> satisfies everything. Only
/// <c>readonly</c> is method-bound — it never satisfies a non-GET route.
/// </summary>
public static class ScopeSatisfaction
{
    public static bool Satisfies(
        IReadOnlyList<Scope> required,
        IReadOnlyList<Scope> granted,
        string method)
    {
        foreach (var scope in required)
        {
            if (Granted(granted, scope, method))
                return true;
        }

        return false;
    }

    private static bool Granted(IReadOnlyList<Scope> granted, Scope required, string method)
    {
        if (granted.Contains(Scope.Operator))
            return true;

        if (required.Equals(Scope.Readonly))
            return granted.Contains(Scope.Readonly) && HttpMethods.IsGet(method);

        return granted.Contains(required);
    }
}
