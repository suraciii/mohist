using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Logging;

/// <summary>
/// Custom <see cref="ILoggerProvider"/> that writes structured (NDJSON) log
/// records to <c>{logDir}/server.log</c>. Each line is a single JSON object
/// matching <see cref="LogRecord"/> so the file is the real source of
/// runtime logs (and so the <c>/api/logs/tail</c> reader can project each
/// line directly into the agreed <c>LogEntry</c> element type without
/// transformation).
/// </summary>
/// <remarks>
/// <para>
/// The file is opened with <see cref="FileShare.ReadWrite"/> so the tail
/// reader can read concurrently while the logger appends.
/// </para>
/// <para>
/// The log directory is created on the first write if it does not exist,
/// so the advertised <c>SystemPaths.Logs</c> value becomes truthful at
/// startup — no setup step needed.
/// </para>
/// <para>
/// Serialization goes through <c>JSON.Options</c> so the on-disk format
/// inherits the same <c>camelCase</c>/encoder settings as the rest of the
/// API pipeline; a record written here is the same shape the tail
/// endpoint emits to the Web client.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider, ILogRecordSink
{
    public const string LogFileName = "server.log";

    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly string _logFilePath;
    private readonly TimeProvider _timeProvider;
    private readonly LogLevel _minimumLevel;
    private StreamWriter? _writer;
    private bool _directoryEnsured;
    private bool _disposed;

    public FileLoggerProvider(ILogPathResolver pathResolver, TimeProvider timeProvider)
        : this(pathResolver, timeProvider, LogLevel.Information)
    {
    }

    public FileLoggerProvider(ILogPathResolver pathResolver, TimeProvider timeProvider, LogLevel minimumLevel)
    {
        var directory = pathResolver.Resolve();
        _logFilePath = Path.Combine(directory, LogFileName);
        _timeProvider = timeProvider;
        _minimumLevel = minimumLevel;
    }

    /// <summary>Exposed for tests so they can drive the time field directly.</summary>
    TimeProvider ILogRecordSink.TimeProvider => _timeProvider;

    /// <summary>Absolute path of the log file the provider writes to.</summary>
    public string LogFilePath => _logFilePath;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimumLevel && !_disposed;

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    void ILogRecordSink.WriteRecord(LogRecord record)
    {
        if (_disposed)
        {
            return;
        }

        var writer = EnsureWriter();
        string line;
        try
        {
            line = JsonSerializer.Serialize(record, JSON.Options);
        }
        catch (Exception ex)
        {
            // The serializer should not fail on a normal LogRecord, but a
            // pathological field value must not take the daemon down. Fall
            // back to a minimal record so the original log line is still
            // observable.
            line = JsonSerializer.Serialize(new LogRecord(
                Level: "ERROR",
                Time: _timeProvider.GetUtcNow(),
                Service: record.Service,
                Message: "failed to serialize log record",
                Exception: ex.ToString()), JSON.Options);
        }

        lock (_writeLock)
        {
            try
            {
                writer.WriteLine(line);
                writer.Flush();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Last-resort: do not let an I/O error on the log file
                // crash the daemon. The console provider will still
                // surface the original record.
                Console.Error.WriteLine($"[Mohist.Server.Logging] failed to write log record: {ex.Message}");
            }
        }
    }

    private StreamWriter EnsureWriter()
    {
        if (_writer is { } outer)
        {
            return outer;
        }

        lock (_writeLock)
        {
            if (_writer is { } inner)
            {
                return inner;
            }

            if (!_directoryEnsured)
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                _directoryEnsured = true;
            }

            var stream = new FileStream(
                _logFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                options: FileOptions.WriteThrough);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,
            };
            return _writer;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_writeLock)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // best-effort
            }
            _writer = null;
        }
        _loggers.Clear();
    }
}
