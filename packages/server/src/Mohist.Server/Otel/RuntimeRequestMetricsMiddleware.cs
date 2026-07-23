using Microsoft.AspNetCore.Routing;

namespace Mohist.Server.Otel;

public sealed class RuntimeRequestMetricsMiddleware(
    RequestDelegate next,
    RuntimeObservability runtime,
    TimeProvider timeProvider)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var agentPath = AgentPath(context.Request.Path);
        var otelRequest = IsOtelRequest(context.Request.Path);
        var createScope = (runtime.Enabled && !otelRequest) || agentPath is not null;
        if (!createScope)
        {
            await next(context);
            return;
        }

        var scope = new RequestWorkScope();
        scope.SetAgentPath(agentPath);
        using var ambient = RequestWorkScope.Push(scope);
        var started = timeProvider.GetTimestamp();
        var exceptional = false;
        try
        {
            await next(context);
        }
        catch
        {
            exceptional = true;
            throw;
        }
        finally
        {
            var completed = timeProvider.GetTimestamp();
            var work = scope.CloseAndSnapshot();
            if (runtime.Enabled)
            {
                var status = exceptional ? 0 : context.Response.StatusCode;
                runtime.CompleteRequest(
                    context.GetEndpoint() is RouteEndpoint endpoint ? endpoint.RoutePattern.RawText : null,
                    context.Request.Method,
                    status,
                    timeProvider.GetElapsedTime(started, completed).TotalMilliseconds,
                    work.DatabaseCalls,
                    work.DownstreamCalls);
                if (work.AgentPath is not null)
                {
                    runtime.RecordAgentPath(work.AgentPath, work.Candidates, work.Processed, work.TranscriptRecords);
                }
            }
        }
    }

    private static bool IsOtelRequest(PathString path) =>
        path.StartsWithSegments("/otel/v1", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/otel/api", StringComparison.OrdinalIgnoreCase);

    private static string? AgentPath(PathString path)
    {
        if (path.StartsWithSegments("/api/agent/status", StringComparison.OrdinalIgnoreCase)
            || path.Value?.Contains("/agent/status", StringComparison.OrdinalIgnoreCase) == true)
            return "agent.status";
        if (path.StartsWithSegments("/api/agent/activity", StringComparison.OrdinalIgnoreCase)
            || path.Value?.Contains("/agent/activity", StringComparison.OrdinalIgnoreCase) == true)
            return "agent.activity";
        return null;
    }
}

public static class RuntimeRequestMetricsApplicationBuilderExtensions
{
    public static IApplicationBuilder UseRuntimeRequestMetrics(this IApplicationBuilder app) =>
        app.UseMiddleware<RuntimeRequestMetricsMiddleware>();
}
