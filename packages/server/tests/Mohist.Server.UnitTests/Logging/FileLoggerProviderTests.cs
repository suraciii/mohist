using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Logging;
using Xunit;

namespace Mohist.Server.UnitTests.Logging;

public class FileLoggerTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 30, 12, 34, 56, 789, TimeSpan.FromHours(8));

    [Fact]
    public void Log_ProjectsLogfmtRecordWithTemplateFields()
    {
        var sink = new RecordingLogRecordSink(new FakeTimeProvider(FixedNow));
        var logger = new FileLogger("Mohist.Server.Runner.Services.DispatchService", sink);

        logger.LogInformation("work claimed");

        var record = Assert.Single(sink.Records);
        Assert.Equal("INFO", record.Level);
        Assert.Equal("server", record.Service);
        Assert.Equal("dispatch", record.Component);
        Assert.Equal(FixedNow, record.Time);
        Assert.Equal("work claimed", record.Message);

        logger.LogInformation("work {WorkId} run {RunId} issue {IssueNumber}", "w_abc", "r_123", 468);

        var fields = Assert.Single(sink.Records.Skip(1)).Fields!;
        Assert.Equal(
            ["workId", "runId", "issueNumber"],
            fields.Select(field => field.Key));
        Assert.Equal("w_abc", fields[0].Value);
        Assert.Equal(468, fields[2].Value);
    }

    [Fact]
    public void Log_ExcludesOriginalFormatAndFormatsExceptionAsOneValue()
    {
        var sink = new RecordingLogRecordSink(new FakeTimeProvider(FixedNow));
        var logger = new FileLogger("Mohist.Server.Runner.Services.ReportService", sink);

        logger.LogError(
            new InvalidOperationException("boom"),
            "report failed for {work}",
            "w_abc");

        var record = Assert.Single(sink.Records);
        Assert.Equal("report", record.Component);
        Assert.Equal(["work"], record.Fields!.Select(field => field.Key));
        Assert.Contains("InvalidOperationException: boom", record.Exception);

        var line = Logfmt.Serialize(record);
        Assert.DoesNotContain("OriginalFormat", line);
        Assert.DoesNotContain('\n', line);
        Assert.Contains("exception=\"", line);
        Assert.True(Logfmt.TryParse(line, out var values));
        Assert.Equal(record.Exception, values["exception"]);
    }

    [Fact]
    public void Log_QuotesAndEscapesLogfmtValues()
    {
        var sink = new RecordingLogRecordSink(new FakeTimeProvider(FixedNow));
        var logger = new FileLogger("Mohist.Server", sink);

        logger.LogInformation("message {Value}", "quote=\" slash=\\ line\nnext");

        var line = Logfmt.Serialize(Assert.Single(sink.Records));
        Assert.Equal(
            "time=2026-06-30T04:34:56.789Z level=INFO msg=\"message quote=\\\" slash=\\\\ line\\nnext\" service=server component=server value=\"quote=\\\" slash=\\\\ line\\nnext\"",
            line);
    }

    [Fact]
    public void IsEnabled_UsesProviderPolicy()
    {
        var logger = new FileLogger(
            "Mohist.Server",
            new RecordingLogRecordSink(new FakeTimeProvider(FixedNow), LogLevel.Warning));

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.False(logger.IsEnabled(LogLevel.None));
    }

    private sealed class RecordingLogRecordSink(
        TimeProvider timeProvider,
        LogLevel minimumLevel = LogLevel.Information)
        : ILogRecordSink
    {
        public List<LogRecord> Records { get; } = [];

        public TimeProvider TimeProvider => timeProvider;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= minimumLevel;

        public void WriteRecord(LogRecord record) => Records.Add(record);
    }
}
