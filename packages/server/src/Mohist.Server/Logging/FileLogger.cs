using System.Text;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Logging;

/// <summary>
/// Single-category <see cref="ILogger"/> that buffers one <see cref="LogRecord"/>
/// at a time and serializes it through the shared <see cref="JSON.Options"/>
/// so the on-disk format matches the API response format by construction.
/// </summary>
/// <remarks>
/// The owning <see cref="FileLoggerProvider"/> is responsible for the file
/// handle and serialization cadence; this class formats one record and
/// hands the resulting bytes to the provider. The formatter does the
/// category-to-service projection (top segment, e.g. <c>Mohist.Server</c>)
/// and normalizes the level to upper-case (e.g. <c>INFO</c>, <c>WARN</c>).
/// </remarks>
internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ILogRecordSink _sink;

    public FileLogger(string categoryName, ILogRecordSink sink)
    {
        _categoryName = categoryName;
        _sink = sink;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
        => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => _sink.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var record = new LogRecord(
            Level: NormalizeLevel(logLevel),
            Time: _sink.TimeProvider.GetUtcNow(),
            Service: ProjectService(_categoryName),
            Message: message,
            Exception: exception?.ToString());

        _sink.WriteRecord(record);
    }

    private static string NormalizeLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        LogLevel.None => "NONE",
        _ => level.ToString().ToUpperInvariant(),
    };

    private static string ProjectService(string categoryName)
    {
        if (string.IsNullOrEmpty(categoryName))
        {
            return string.Empty;
        }

        // The agreed "service" is the assembly-level identity, not the
        // root namespace. For Mohist's own categories
        // (Mohist.Server.<area>.<...>), the first two segments —
        // "Mohist.Server" — match the OpenTelemetry service name. For
        // third-party categories (Microsoft.AspNetCore.<...>) we take
        // everything up to the second dot so the projected service
        // remains stable regardless of how deep the category goes.
        var first = categoryName.IndexOf('.');
        if (first < 0)
        {
            return categoryName;
        }

        var second = categoryName.IndexOf('.', first + 1);
        return second < 0 ? categoryName : categoryName[..second];
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
