using Xunit;

namespace Mohist.Server.SpecTests.Support;

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
