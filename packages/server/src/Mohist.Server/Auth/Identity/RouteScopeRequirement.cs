using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// Endpoint metadata declaring the scopes a route requires. A credential
/// satisfies the route when it satisfies any declared scope; a route
/// without this metadata falls back to the method-based default: GET is
/// the business observation surface (operator or readonly), every other
/// method requires operator (docs/auth.md scope table and sensitive
/// infrastructure surface attribution).
/// </summary>
public sealed record RouteScopeRequirement(IReadOnlyList<Scope> Scopes);

public static class RouteScopeRequirementExtensions
{
    /// <summary>Sensitive infrastructure surface: only operator credentials.</summary>
    public static readonly IReadOnlyList<Scope> Operator = [Scope.Operator];

    /// <summary>Business observation surface: operator or readonly (GET only).</summary>
    public static readonly IReadOnlyList<Scope> OperatorOrReadonly = [Scope.Operator, Scope.Readonly];

    /// <summary>Runner machine surface: operator or runner-bound credentials.</summary>
    public static readonly IReadOnlyList<Scope> Runner = [Scope.Runner];

    public static TBuilder RequireScopes<TBuilder>(this TBuilder builder, params Scope[] scopes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(new RouteScopeRequirement(scopes)));
        return builder;
    }
}
