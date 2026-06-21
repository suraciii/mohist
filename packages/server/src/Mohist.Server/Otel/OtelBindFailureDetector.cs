using System.Net.Sockets;

namespace Mohist.Server.Otel;

/// <summary>
/// Helpers for diagnosing why a Kestrel bind attempt failed. Used by
/// <c>Program.cs</c> to decide whether an <see cref="IOException"/>
/// surfacing from <c>app.StartAsync()</c> can be classified as an
/// OTLP-port bind failure (in which case the main API should keep
/// starting) vs. an unrelated host-start error that must abort the
/// process.
/// </summary>
public static class OtelBindFailureDetector
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> looks like a
    /// Kestrel "address already in use" failure for the configured
    /// OTLP port. The check is intentionally narrow: it only matches
    /// the well-known Kestrel bind-failure message and only when the
    /// address string in the message references the OTLP port under
    /// one of the supported bind hosts.
    /// </summary>
    /// <remarks>
    /// Kestrel's exception text format is
    /// <c>Failed to bind to address http://&lt;host&gt;:&lt;port&gt;: address already in use.</c>
    /// We accept any of the well-known bind hosts (<c>127.0.0.1</c>,
    /// <c>0.0.0.0</c>, <c>localhost</c>) so the check works whether
    /// the operator bound the port explicitly to one of those or used
    /// the wildcard.
    /// </remarks>
    public static bool IsOtlpPortBindFailure(IOException ex, int otlpPort)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var msg = ex.Message ?? string.Empty;
        if (!msg.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            return false;
        return msg.Contains($"127.0.0.1:{otlpPort}", StringComparison.Ordinal)
            || msg.Contains($"0.0.0.0:{otlpPort}", StringComparison.Ordinal)
            || msg.Contains($"[::]:{otlpPort}", StringComparison.Ordinal)
            || msg.Contains($"localhost:{otlpPort}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Allocates an ephemeral local port by briefly binding a
    /// <see cref="TcpListener"/> and reading the assigned port. Useful
    /// for tests that need to seed a port collision without hard-coding
    /// a value that may be in use on a developer machine.
    /// </summary>
    public static int AllocateEphemeralLoopbackPort()
    {
        var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }
}
