using System.Net.Sockets;
using System.IO;
using Mohist.Server.Otel;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Telemetry;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.Telemetry)]
public class OtelBindFailureDetectorSpecs
{
    [Fact]
    public void IsOtlpPortBindFailure_TrueForMatchingLoopbackMessage()
    {
        var ex = new IOException(
            "Failed to bind to address http://127.0.0.1:4318: address already in use.");

        Assert.True(OtelBindFailureDetector.IsOtlpPortBindFailure(ex, 4318));
    }

    [Fact]
    public void IsOtlpPortBindFailure_TrueForWildcardBind()
    {
        var ex = new IOException(
            "Failed to bind to address http://0.0.0.0:14318: address already in use.");

        Assert.True(OtelBindFailureDetector.IsOtlpPortBindFailure(ex, 14318));
    }

    [Fact]
    public void IsOtlpPortBindFailure_TrueForLocalhostHost()
    {
        var ex = new IOException(
            "Failed to bind to address http://localhost:4318: address already in use.");

        Assert.True(OtelBindFailureDetector.IsOtlpPortBindFailure(ex, 4318));
    }

    [Fact]
    public void IsOtlpPortBindFailure_TrueForIpv6Wildcard()
    {
        var ex = new IOException(
            "Failed to bind to address http://[::]:4318: address already in use.");

        Assert.True(OtelBindFailureDetector.IsOtlpPortBindFailure(ex, 4318));
    }

    [Fact]
    public void IsOtlpPortBindFailure_FalseForDifferentPort()
    {
        var ex = new IOException(
            "Failed to bind to address http://127.0.0.1:9999: address already in use.");

        Assert.False(OtelBindFailureDetector.IsOtlpPortBindFailure(ex, 4318));
    }

    [Fact]
    public void IsOtlpPortBindFailure_FalseForMainApiCollision()
    {
        // Main API port collision is NOT a recoverable OTLP error —
        // it's a hard startup failure.
        var ex = new IOException(
            "Failed to bind to address http://127.0.0.1:3456: address already in use.");

        Assert.False(OtelBindFailureDetector.IsOtlpPortBindFailure(ex, 4318));
    }

    [Fact]
    public void IsOtlpPortBindFailure_FalseForUnrelatedException()
    {
        var ex = new IOException("Some other network failure.");

        Assert.False(OtelBindFailureDetector.IsOtlpPortBindFailure(ex, 4318));
    }

    [Fact]
    public void IsOtlpPortBindFailure_TrueRegardlessOfMessageCasing()
    {
        var ex = new IOException(
            "Failed to bind to address http://127.0.0.1:4318: ADDRESS ALREADY IN USE.");

        Assert.True(OtelBindFailureDetector.IsOtlpPortBindFailure(ex, 4318));
    }

    [Fact]
    public void AllocateEphemeralLoopbackPort_ReturnsUsablePort()
    {
        var port = OtelBindFailureDetector.AllocateEphemeralLoopbackPort();

        // Sanity: the port should be in the ephemeral range and not
        // match the well-known service ports.
        Assert.InRange(port, 1024, 65535);
        Assert.NotEqual(4318, port);
    }
}
