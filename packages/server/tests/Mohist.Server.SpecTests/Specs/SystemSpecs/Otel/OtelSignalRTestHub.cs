using Microsoft.AspNetCore.SignalR;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs.Otel;

/// <summary>
/// Minimal hub used by SignalR-instrumentation tests. The single
/// <c>Echo</c> method exists so the SignalR dispatcher emits a
/// <c>Microsoft.AspNetCore.SignalR.Server</c> activity carrying a
/// <c>hub.method</c> attribute the assertions can pin against.
/// </summary>
public sealed class OtelSignalRTestHub : Hub
{
    public string Echo(string value) => value;
}