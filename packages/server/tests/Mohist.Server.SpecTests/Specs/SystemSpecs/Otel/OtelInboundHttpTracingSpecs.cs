using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs.Otel;

/// <summary>
/// Integration tests for the inbound HTTP tracing pipeline. Each test
/// stands up a minimal <see cref="OtelTestHost"/> whose service
/// registration mirrors <c>ConfigureMohistServices</c>'s
/// <see cref="MohistOpenTelemetryRegistration.AddMohistOpenTelemetry"/>
/// call. The host uses an in-memory
/// <see cref="RecordingActivityProcessor"/> to capture every
/// <see cref="Activity"/> the OTel pipeline emits so tests can assert
/// which spans were (and were not) produced.
/// </summary>
[Collection("OtelTracing")]
public class OtelInboundHttpTracingSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InboundHttpRequest_MappedRoute_ProducesExactlyOneAspNetCoreSpan()
    {
        await using var host = new OtelTestHost(new OtelTestHostOptions { Enabled = true });
        using var client = host.CreateClient();

        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var spans = await host.Recorder.WaitForAsync(s => s.Any(IsInboundHttpSpan));

        var inbound = spans.Where(IsInboundHttpSpan).ToList();
        Assert.Single(inbound);

        var span = inbound[0];
        Assert.Equal("/api/health", span.GetTagItem("http.route"));
        Assert.Equal(200, span.GetTagItem("http.response.status_code"));
        Assert.True(span.Duration >= TimeSpan.Zero);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InboundHttpRequest_OtelPathPrefix_ProducesNoInboundSpan()
    {
        await using var host = new OtelTestHost(new OtelTestHostOptions { Enabled = true });
        using var client = host.CreateClient();

        var response = await client.PostAsJsonAsync("/otel/v1/traces", new { resourceSpans = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var inbound = host.Recorder.EndedActivities.Where(IsInboundHttpSpan).ToList();
        Assert.Empty(inbound);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void ExcludeOtelIngestPath_ReturnsFalse_ForOtelPathPrefix()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/otel/v1/traces";
        Assert.False(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(ctx));

        ctx.Request.Path = "/otel";
        Assert.False(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(ctx));

        // Segment match, not prefix match: /otel-anything-else must NOT
        // be excluded, otherwise the filter is over-eager and would
        // silence unrelated /otelware-style URLs.
        ctx.Request.Path = "/otel-anything-else";
        Assert.True(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(ctx));

        ctx.Request.Path = "/api/health";
        Assert.True(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(ctx));
    }

    private static bool IsInboundHttpSpan(Activity activity)
    {
        if (activity.Source is null) return false;
        return activity.Source.Name == "Microsoft.AspNetCore"
            || activity.OperationName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal);
    }
}
