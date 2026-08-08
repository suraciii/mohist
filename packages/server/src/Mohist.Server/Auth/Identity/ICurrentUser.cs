using Mohist.Server.Auth.Domain;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// The authenticated subject of the current request: the resolved
/// <see cref="MohistPrincipal"/> behind every mutating handler's actor
/// attribution. The principal is placed in <see cref="HttpContext"/> items
/// by <see cref="AuthResolutionMiddleware"/> before handlers run; this
/// accessor is the typed seam handlers depend on instead of reading the
/// context directly.
/// </summary>
public interface ICurrentUser
{
    MohistPrincipal Principal { get; }
}
