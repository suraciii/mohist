using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Mohist.Server.Logging;

public sealed class FileLoggerProvider : ILoggerProvider, ILogRecordSink
{
    public const string LogFileName = "server.log";
    public const long MaxLogFileBytes = 32L * 1024 * 1024;

    private const int KeptGenerations = 2;

    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly string _logFilePath;
    private readonly TimeProvider _timeProvider;
    private readonly LogLevel _minimumLevel;
    private readonly ILogFileSystem _fileSystem;
    private readonly long _maxLogFileBytes;
    private StreamWriter? _writer;
    private long _currentFileBytes;
    private bool _directoryEnsured;
    private volatile bool _disposed;

    public FileLoggerProvider(ILogPathResolver pathResolver, TimeProvider timeProvider)
        : this(pathResolver, timeProvider, LogLevel.Information, new PhysicalLogFileSystem(), MaxLogFileBytes)
    {
    }

    public FileLoggerProvider(ILogPathResolver pathResolver, TimeProvider timeProvider, LogLevel minimumLevel)
        : this(pathResolver, timeProvider, minimumLevel, new PhysicalLogFileSystem(), MaxLogFileBytes)
    {
    }

    internal FileLoggerProvider(
        ILogPathResolver pathResolver,
        TimeProvider timeProvider,
        LogLevel minimumLevel,
        ILogFileSystem fileSystem,
        long maxLogFileBytes = MaxLogFileBytes)
    {
        if (maxLogFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLogFileBytes));

        _logFilePath = Path.Combine(pathResolver.Resolve(), LogFileName);
        _timeProvider = timeProvider;
        _minimumLevel = minimumLevel;
        _fileSystem = fileSystem;
        _maxLogFileBytes = maxLogFileBytes;
    }

    TimeProvider ILogRecordSink.TimeProvider => _timeProvider;

    public string LogFilePath => _logFilePath;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && logLevel >= _minimumLevel && !_disposed;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    void ILogRecordSink.WriteRecord(LogRecord record)
    {
        if (_disposed)
            return;

        string line;
        try
        {
            line = Logfmt.Serialize(record);
        }
        catch (Exception ex)
        {
            line = Logfmt.Serialize(new LogRecord(
                Level: "ERROR",
                Time: _timeProvider.GetUtcNow(),
                Service: "server",
                Message: "failed to serialize log record",
                Exception: ex.ToString(),
                Component: record.Component));
        }

        lock (_writeLock)
        {
            if (_disposed)
                return;

            try
            {
                var writer = EnsureWriterLocked();
                var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (_currentFileBytes > 0 && _currentFileBytes + lineBytes > _maxLogFileBytes)
                {
                    RotateLocked();
                    writer = EnsureWriterLocked();
                }

                writer.Write(line);
                writer.Write('\n');
                writer.Flush();
                _currentFileBytes += lineBytes;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                AbandonWriterLocked();
                ReportWriteFailure(ex);
            }
        }
    }

    private StreamWriter EnsureWriterLocked()
    {
        if (_writer is not null)
            return _writer;

        if (!_directoryEnsured)
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory))
                _fileSystem.CreateDirectory(directory);
            _directoryEnsured = true;
        }

        var stream = _fileSystem.OpenAppend(_logFilePath);
        _currentFileBytes = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return _writer;
    }

    private void RotateLocked()
    {
        _writer?.Dispose();
        _writer = null;
        _currentFileBytes = 0;

        for (var generation = KeptGenerations - 1; generation >= 1; generation--)
        {
            var source = _logFilePath + $".{generation}";
            var destination = _logFilePath + $".{generation + 1}";
            if (!_fileSystem.Exists(source))
                continue;

            _fileSystem.Delete(destination);
            _fileSystem.Move(source, destination);
        }

        if (_fileSystem.Exists(_logFilePath))
            _fileSystem.Move(_logFilePath, _logFilePath + ".1");
    }

    private void AbandonWriterLocked()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
        }

        _writer = null;
        _currentFileBytes = 0;
    }

    private static void ReportWriteFailure(Exception exception)
    {
        try
        {
            Console.Error.WriteLine($"[Mohist.Server.Logging] failed to write log record: {exception.Message}");
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
            }

            _writer = null;
            _currentFileBytes = 0;
        }

        _loggers.Clear();
    }
}

internal interface ILogFileSystem
{
    bool Exists(string path);
    void CreateDirectory(string path);
    void Delete(string path);
    void Move(string source, string destination);
    Stream OpenAppend(string path);
}

internal sealed class PhysicalLogFileSystem : ILogFileSystem
{
    public bool Exists(string path) => File.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public void Move(string source, string destination) => File.Move(source, destination, overwrite: true);

    public Stream OpenAppend(string path) => new FileStream(
        path,
        FileMode.Append,
        FileAccess.Write,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 4096,
        options: FileOptions.WriteThrough);
}
