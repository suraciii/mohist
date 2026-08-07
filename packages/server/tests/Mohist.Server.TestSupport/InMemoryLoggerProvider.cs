using Microsoft.Extensions.Logging;

namespace Mohist.Server.TestSupport;

public sealed class InMemoryLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => NoopLogger.Instance;

    public void Dispose()
    {
    }

    private sealed class NoopLogger : ILogger
    {
        public static readonly NoopLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
