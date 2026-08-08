using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Auth.Identity;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class DeviceRateLimiterTests
{
    [Fact]
    public void IsAllowed_AdmitsUpToTheLimitPerWindow_ThenDenies()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var limiter = new DevicePollRateLimiter(time);

        for (var attempt = 1; attempt <= DevicePollRateLimiter.LimitPerMinute; attempt++)
            Assert.True(limiter.IsAllowed("1.2.3.4"));

        Assert.False(limiter.IsAllowed("1.2.3.4"));
    }

    [Fact]
    public void IsAllowed_AWindowLater_ResetsTheCount()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var limiter = new DevicePollRateLimiter(time);
        for (var attempt = 1; attempt <= DevicePollRateLimiter.LimitPerMinute; attempt++)
            limiter.IsAllowed("1.2.3.4");

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(limiter.IsAllowed("1.2.3.4"));
    }

    [Fact]
    public void IsAllowed_TracksSourcesIndependently()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var limiter = new DeviceGuessRateLimiter(time);

        for (var attempt = 1; attempt <= DeviceGuessRateLimiter.LimitPerMinute; attempt++)
            limiter.IsAllowed("1.2.3.4");

        Assert.False(limiter.IsAllowed("1.2.3.4"));
        Assert.True(limiter.IsAllowed("5.6.7.8"));
        Assert.True(limiter.IsAllowed("unknown"));
    }

    [Fact]
    public void GuessLimiter_AndPollLimiter_HaveIndependentLimits()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var poll = new DevicePollRateLimiter(time);
        var guess = new DeviceGuessRateLimiter(time);

        for (var attempt = 1; attempt <= DeviceGuessRateLimiter.LimitPerMinute; attempt++)
        {
            Assert.True(guess.IsAllowed("1.2.3.4"));
            poll.IsAllowed("1.2.3.4");
        }

        // The guess limiter is exhausted; the poll limiter (higher
        // limit) still has headroom for the same source.
        Assert.False(guess.IsAllowed("1.2.3.4"));
        Assert.True(poll.IsAllowed("1.2.3.4"));
    }
}
