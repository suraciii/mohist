using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Auth.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class PatPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoTtl_DefaultsTo90Days()
    {
        var time = new FakeTimeProvider(Now);

        var expiresAt = PatPolicy.ResolveExpiresAt(ttlHours: null, time);

        Assert.Equal(Now.AddDays(90), expiresAt);
    }

    [Fact]
    public void ExplicitTtl_IsRespected()
    {
        var time = new FakeTimeProvider(Now);

        var expiresAt = PatPolicy.ResolveExpiresAt(ttlHours: 720, time);

        Assert.Equal(Now.AddHours(720), expiresAt);
    }

    [Fact]
    public void TtlOfOneYear_IsAccepted()
    {
        var time = new FakeTimeProvider(Now);

        var expiresAt = PatPolicy.ResolveExpiresAt(PatPolicy.MaxTtlHours, time);

        Assert.Equal(Now.AddHours(PatPolicy.MaxTtlHours), expiresAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(24 * 365 + 1)]
    public void TtlOutsideTheAllowedRange_IsRejected(int ttlHours)
    {
        var time = new FakeTimeProvider(Now);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PatPolicy.ResolveExpiresAt(ttlHours, time));
    }
}
