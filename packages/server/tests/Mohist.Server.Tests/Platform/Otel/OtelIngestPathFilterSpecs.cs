using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Hosting;
using Xunit;

namespace Mohist.Server.Tests.Platform.Otel;

[Trait("level", "L0")]
public sealed class OtelIngestPathFilterSpecs
{
    [Fact]
    public void ExcludeOtelIngestPath_ReturnsFalse_ForOtelPathPrefix()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/otel/v1/traces";
        Assert.False(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(context));

        context.Request.Path = "/otel";
        Assert.False(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(context));

        // Segment match, not prefix match: /otel-anything-else must NOT
        // be excluded, otherwise the filter is over-eager and would
        // silence unrelated /otelware-style URLs.
        context.Request.Path = "/otel-anything-else";
        Assert.True(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(context));

        context.Request.Path = "/api/health";
        Assert.True(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(context));
    }
}
