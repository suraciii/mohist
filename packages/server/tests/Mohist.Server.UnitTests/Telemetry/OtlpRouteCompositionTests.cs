using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public sealed class OtlpRouteCompositionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task MapOtlpRoutes_TracksConfiguredEnablement(bool enabled, bool expectedRoute)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.Configure<OtelOptions>(options => options.Enabled = enabled);
        builder.Services.AddSingleton<TraceIngester>(_ => throw new InvalidOperationException("Not resolved by composition test."));
        builder.Services.AddSingleton<OtlpTraceResponseWriter>(_ => throw new InvalidOperationException("Not resolved by composition test."));
        builder.Services.AddSingleton<IOtlpIngestGate>(_ => throw new InvalidOperationException("Not resolved by composition test."));
        await using var app = builder.Build();

        app.MapOtlpRoutes();

        var hasRoute = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Any(endpoint => string.Equals(
                endpoint.RoutePattern.RawText,
                OtlpRoutes.OtlpTracesPath,
                StringComparison.Ordinal));
        Assert.Equal(expectedRoute, hasRoute);
    }
}
