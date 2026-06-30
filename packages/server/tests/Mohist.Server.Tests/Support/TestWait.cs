using Xunit;

namespace Mohist.Server.Tests.Support;

/// <summary>
/// Poll-based wait helpers for tests. These bound the wait with
/// <see cref="CancellationTokenSource"/> cancellation rather than
/// comparing against the wall clock (<c>while(DateTime.UtcNow &lt; deadline)</c>),
/// which the testing principles forbid.
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
            var attempts = Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds / Math.Max(1, step.TotalMilliseconds)));
            T current = default!;
            for (var i = 0; i <= attempts; i++)
            {
                current = await probe();
                if (isDone(current))
                    return current;
                if (i == attempts)
                    break;
                await advance();
                await Task.Yield();
            }
            Assert.Fail($"Timed out waiting for: {description}. Last value: {current}");
            return default!;
        }

        using var cts = new CancellationTokenSource(timeout);
        var token = cts.Token;
        T last = default!;
        try
        {
            while (true)
            {
                last = await probe();
                if (isDone(last))
                    return last;
                await Task.Delay(step, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Assert.Fail($"Timed out waiting for: {description}. Last value: {last}");
            return default!;
        }
    }

    public static async Task ForAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan step,
        string description,
        Func<Task>? advance = null)
    {
        if (advance is not null)
        {
            var attempts = Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds / Math.Max(1, step.TotalMilliseconds)));
            for (var i = 0; i <= attempts; i++)
            {
                if (condition())
                    return;
                if (i == attempts)
                    break;
                await advance();
                await Task.Yield();
            }
            Assert.Fail($"Timed out waiting for: {description}.");
            return;
        }

        using var cts = new CancellationTokenSource(timeout);
        var token = cts.Token;
        try
        {
            while (!condition())
                await Task.Delay(step, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Assert.Fail($"Timed out waiting for: {description}.");
        }
    }
}
