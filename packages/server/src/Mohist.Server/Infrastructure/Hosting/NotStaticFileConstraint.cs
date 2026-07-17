using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Server.Infrastructure.Hosting;

public sealed class NotStaticFileConstraint : IRouteConstraint
{
    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        if (routeDirection == RouteDirection.UrlGeneration)
        {
            return true;
        }

        if (httpContext is null)
        {
            return true;
        }

        if (!values.TryGetValue(routeKey, out var value) || value is not string segment || segment.Length == 0)
        {
            return true;
        }

        var files = httpContext.RequestServices.GetService<IWebContentProvider>()?.Files;
        if (files is null)
        {
            return true;
        }

        return !files.GetFileInfo("/" + segment.TrimStart('/')).Exists;
    }
}
