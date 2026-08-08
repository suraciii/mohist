using System.Collections.Concurrent;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// Per-source fixed-window rate limiter for the device-authorization
/// endpoints: polling the token endpoint and guessing user codes must
/// stay within a few attempts per minute per source. The source key is
/// the caller's IP; state is
/// in-memory and windowed, so a single-process server needs no shared
/// store. Stale windows are swept when the table grows, never on a
/// timer, keeping the limiter deterministic under a fake clock.
/// </summary>
public abstract class FixedWindowRateLimiter : ISingletonService
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly int _limit;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private const int SweepThreshold = 10_000;

    protected FixedWindowRateLimiter(TimeProvider time, TimeSpan window, int limit)
    {
        _time = time;
        _window = window;
        _limit = limit;
    }

    /// <summary>
    /// True when the source is inside its limit for the current window.
    /// The caller decides what to do with a denied source (slow_down
    /// for polling, 429 for guessing).
    /// </summary>
    public bool IsAllowed(string key)
    {
        if (string.IsNullOrEmpty(key))
            key = "unknown";

        var now = _time.GetUtcNow();
        var windowStart = now - new TimeSpan(now.Ticks % _window.Ticks);
        var entry = _windows.AddOrUpdate(
            key,
            static (_, start) => new Window(start, 1),
            static (_, window, start) =>
                window.Start == start
                    ? window with { Count = window.Count + 1 }
                    : new Window(start, 1),
            windowStart);

        if (_windows.Count > SweepThreshold)
            Sweep(windowStart);

        return entry.Count <= _limit;
    }

    private void Sweep(DateTimeOffset currentWindowStart)
    {
        foreach (var (key, window) in _windows)
        {
            if (window.Start != currentWindowStart)
                _windows.TryRemove(key, out _);
        }
    }

    private sealed record Window(DateTimeOffset Start, int Count);
}

/// <summary>Limits device-code polling to a compliant client's pace: RFC 8628's
/// five-second minimum interval permits twelve polls per minute; the next
/// one earns a slow_down.</summary>
public sealed class DevicePollRateLimiter : FixedWindowRateLimiter
{
    public DevicePollRateLimiter(TimeProvider time)
        : base(time, TimeSpan.FromMinutes(1), LimitPerMinute)
    {
    }

    internal const int LimitPerMinute = 12;
}

/// <summary>Limits user-code guessing on the confirmation-page verify
/// endpoint.</summary>
public sealed class DeviceGuessRateLimiter : FixedWindowRateLimiter
{
    public DeviceGuessRateLimiter(TimeProvider time)
        : base(time, TimeSpan.FromMinutes(1), LimitPerMinute)
    {
    }

    internal const int LimitPerMinute = 10;
}
