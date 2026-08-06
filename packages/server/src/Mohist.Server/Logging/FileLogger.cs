using Microsoft.Extensions.Logging;

namespace Mohist.Server.Logging;

internal sealed class FileLogger : ILogger
{
    private const string ServiceName = "server";

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
            Service: ServiceName,
            Message: message,
            Exception: exception?.ToString(),
            Fields: ProjectFields(state),
            Component: ProjectComponent(_categoryName));

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
        _ => level.ToString().ToUpperInvariant(),
    };

    private static IReadOnlyList<KeyValuePair<string, object?>>? ProjectFields<TState>(TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
            return null;

        var fields = new List<KeyValuePair<string, object?>>(values.Count);
        foreach (var pair in values)
        {
            if (pair.Key is "{OriginalFormat}" or "OriginalFormat")
                continue;

            var key = LowerFirst(pair.Key);
            if (key.Length == 0 || IsReservedKey(key))
                continue;

            fields.Add(new(key, pair.Value));
        }

        return fields.Count == 0 ? null : fields;
    }

    private static bool IsReservedKey(string key) => key is
        "time" or "level" or "msg" or "service" or "component" or "exception";

    private static string ProjectComponent(string categoryName)
    {
        var lastSegment = categoryName[(categoryName.LastIndexOf('.') + 1)..];
        foreach (var suffix in new[] { "Service", "Grain", "Handler", "Routes", "Provider" })
        {
            if (lastSegment.Length > suffix.Length
                && lastSegment.EndsWith(suffix, StringComparison.Ordinal))
            {
                lastSegment = lastSegment[..^suffix.Length];
                break;
            }
        }

        return LowerFirst(lastSegment);
    }

    private static string LowerFirst(string value)
    {
        if (value.Length == 0)
            return value;

        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
