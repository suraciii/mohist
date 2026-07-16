namespace Mohist.Server.Logging;

internal interface ILogRecordSink
{
    TimeProvider TimeProvider { get; }

    bool IsEnabled(LogLevel logLevel);

    void WriteRecord(LogRecord record);
}
