using Mohist.Server.Runner.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Runner;

public class RunnerBuildIdentityPolicyTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" abc123 ", "abc123")]
    public void Normalize_ReturnsCanonicalIdentity(string? value, string? expected)
    {
        Assert.Equal(expected, RunnerBuildIdentityPolicy.Normalize(value));
    }

    [Fact]
    public void ResolveForRegister_UsesExplicitThenRuntimeThenPendingIdentity()
    {
        Assert.Equal("explicit", RunnerBuildIdentityPolicy.ResolveForRegister("explicit", "runtime", "pending"));
        Assert.Equal("runtime", RunnerBuildIdentityPolicy.ResolveForRegister(null, "runtime", "pending"));
        Assert.Equal("pending", RunnerBuildIdentityPolicy.ResolveForRegister(null, null, "pending"));
        Assert.Null(RunnerBuildIdentityPolicy.ResolveForRegister(null, null, null));
    }

    [Fact]
    public void ResolveForHeartbeat_UsesIncomingThenPendingThenCurrentIdentity()
    {
        Assert.Equal("incoming", RunnerBuildIdentityPolicy.ResolveForHeartbeat("incoming", "pending", "current"));
        Assert.Equal("pending", RunnerBuildIdentityPolicy.ResolveForHeartbeat(null, "pending", "current"));
        Assert.Equal("current", RunnerBuildIdentityPolicy.ResolveForHeartbeat(null, null, "current"));
        Assert.Null(RunnerBuildIdentityPolicy.ResolveForHeartbeat(null, null, null));
    }
}
