using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// Reads the request's resolved principal from the
/// <see cref="HttpContext.Items"/> slot written by
/// <see cref="AuthResolutionMiddleware"/>. Scoped to the request; every
/// non-exempt surface route carries a principal, so a missing slot is a
/// middleware-ordering bug and fails loudly.
/// </summary>
public sealed class HttpContextCurrentUser : ICurrentUser, IScopedService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public MohistPrincipal Principal =>
        _httpContextAccessor.HttpContext?.Items[MohistPrincipal.HttpContextItemKey] as MohistPrincipal
        ?? throw new InvalidOperationException(
            "No authenticated principal on the current request; the auth middleware did not run.");
}
