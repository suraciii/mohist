using Xunit;

namespace Mohist.Server.ComponentSpecs.Support;

/// <summary>
/// Poll-based wait helpers for tests. The retry budget is computed from the
/// supplied timeout/step pair but does not use wall-clock timers; each retry
/// yields to let queued async work run, or invokes the supplied fake-time
/// advance hook when time progression is part of the behavior under test.
/// </summary>
/// <remarks>
/// Prefer a deterministic signal (<c>TaskCompletionSource</c>, event await,
/// injected <c>TimeProvider</c> advance) whenever one exists. Use these only
/// for asynchronous convergence that has no single signal to hook (e.g. grain
/// state settling across Orleans turns, multi-hop event-bus delivery). The
/// <paramref name="description"/> is surfaced on timeout so failures are loud.
/// </remarks>
public static class TestWait
{
    public static async Task<T> ForAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> isDone,
        TimeSpan timeout,
        TimeSpan step,
        string description,
        Func<Task>? advance = null)
    {
        if (advance is not null)
        {
        }

        var attempts = Attempts(timeout, step);
        T current = default!;
        for (var i = 0; i <= attempts; i++)
        {
            current = await probe();
            if (isDone(current))
                return current;
            if (i == attempts)
                break;
            if (advance is not null)
                await advance();
            await Task.Yield();
        }

        Assert.Fail($"Timed out waiting for: {description}. Last value: {current}");
        return default!;
    }

    public static async Task ForAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan step,
        string description,
        Func<Task>? advance = null)
    {
        var attempts = Attempts(timeout, step);
        for (var i = 0; i <= attempts; i++)
        {
            if (condition())
                return;
            if (i == attempts)
                break;
            if (advance is not null)
                await advance();
            await Task.Yield();
        }

        Assert.Fail($"Timed out waiting for: {description}.");
    }

    private static int Attempts(TimeSpan timeout, TimeSpan step) =>
        Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds / Math.Max(1, step.TotalMilliseconds)));
}
