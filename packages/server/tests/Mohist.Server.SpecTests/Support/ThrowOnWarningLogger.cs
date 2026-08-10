using Microsoft.Extensions.Logging;

namespace Mohist.Server.SpecTests.Support;

public sealed class ThrowOnWarningLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (level == LogLevel.Warning)
            throw new InvalidOperationException(formatter(state, exception));
    }
}
