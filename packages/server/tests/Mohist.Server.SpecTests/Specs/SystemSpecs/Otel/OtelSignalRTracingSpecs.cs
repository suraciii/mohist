using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs.Otel;

/// <summary>
/// Integration coverage for the SignalR hub-method ActivitySource from
/// design Decision 3 and spec scenario "SignalR hub method
/// invocations are traced as child spans".
///
/// <para>
/// These tests stand up a fresh <see cref="OtelTestHost"/> with the
/// production OTel pipeline plus a <see cref="OtelSignalRTestHub"/>
/// mapped at <c>/hubs/test</c>. A real SignalR client
/// (<c>HubConnectionBuilder</c>) connects through the in-process
/// <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> handler so
/// the full server-side hub dispatcher runs and the source emits its
/// activity. The <see cref="RecordingActivityProcessor"/> captures it.
/// </para>
/// </summary>
[Collection("OtelTracing")]
public class OtelSignalRTracingSpecs
{
    [Fact]
    public async Task HubConnection_ProducesRealEchoHubMethodActivity()
    {
        // Stand up an OtelTestHost with the production OTel pipeline,
        // plus our minimal test hub mapped at /hubs/test.
        await using var host = new OtelTestHost(new OtelTestHostOptions
        {
            Enabled = true,
            ConfigureServices = services => services.AddSignalR(),
            ConfigureApp = app =>
            {
                app.MapHub<OtelSignalRTestHub>("/hubs/test", options =>
                {
                    // Long-polling is the default transport for
                    // TestServer (no actual WebSocket binding on the
                    // in-process pipeline); we leave it explicit so
                    // future maintainers see why we don't set
                    // WebSockets here.
                    options.Transports = HttpTransportType.LongPolling;
                });
            },
        });

        // Open a SignalR client connection routed through the
        // in-process TestServer handler. HubConnectionBuilder's
        // HttpConnection is what drives the negotiate + long-poll
        // round trips through the host's pipeline — exactly what a
        // browser or runner would do over the wire.
        var serverHandler = host.TestServer.CreateHandler();
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/test", options =>
            {
                options.HttpMessageHandlerFactory = _ => serverHandler;
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await connection.StartAsync();

        try
        {
            var echo = await connection.InvokeAsync<string>("Echo", "hello-otel");
            Assert.Equal("hello-otel", echo);
        }
        finally
        {
            await connection.DisposeAsync();
        }

        // The SignalR Server source emits an activity for every hub
        // lifecycle hook and every hub method invocation. Wait until
        // at least one Server-kind activity (the OnConnectedAsync
        // hook fired at connection establishment) has been captured.
        await host.Recorder.WaitForAsync(s => s
            .Any(a => a.Source?.Name == MohistOpenTelemetryRegistration.SignalRServerActivitySourceName));

        var signalrActivities = host.Recorder.EndedActivities
            .Where(a => a.Source?.Name == MohistOpenTelemetryRegistration.SignalRServerActivitySourceName)
            .ToList();
        Assert.NotEmpty(signalrActivities);

        var inboundGet = host.Recorder.EndedActivities
            .Where(a => a.Source?.Name == "Microsoft.AspNetCore"
                && a.Kind == ActivityKind.Server
                && a.DisplayName.StartsWith("GET", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(inboundGet);

        var echoActivity = Assert.Single(signalrActivities, a =>
            string.Equals(a.GetTagItem("rpc.method") as string, "Echo", StringComparison.Ordinal));
        Assert.NotEqual(default, echoActivity.TraceId);
        Assert.NotEqual(default, echoActivity.SpanId);
        Assert.Equal("signalr", echoActivity.GetTagItem("rpc.system"));
        Assert.All(signalrActivities, a => Assert.NotEqual(default, a.TraceId));

        // Every captured hub activity (whether OnConnectedAsync,
        // OnDisconnectedAsync, or a method like Echo) MUST carry
        // the rpc.method / rpc.system tags that downstream
        // collectors group on. The lifecycle hooks carry their
        // method name (OnConnectedAsync, OnDisconnectedAsync) and
        // user-invoked methods (Echo) carry theirs. .NET's SignalR
        // source uses the OpenTelemetry semantic conventions
        // rpc.method / rpc.system / rpc.service.
        foreach (var hubActivity in signalrActivities)
        {
            Assert.NotNull(hubActivity.GetTagItem("rpc.method"));
            Assert.NotNull(hubActivity.GetTagItem("rpc.system"));
            Assert.Equal("signalr", hubActivity.GetTagItem("rpc.system"));
        }
    }
}
