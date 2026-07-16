using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Logging;
using Xunit;

namespace Mohist.Server.UnitTests.Logging;

public class FileLoggerTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 30, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Log_RecordsLevelTimeServiceAndMessage()
    {
        var sink = new RecordingLogRecordSink(new FakeTimeProvider(FixedNow));
        var logger = new FileLogger("Mohist.Server.Workflow.Grains", sink);

        logger.LogInformation("hello world");

        var record = Assert.Single(sink.Records);
        Assert.Equal("INFO", record.Level);
        Assert.Equal("Mohist.Server", record.Service);
        Assert.Equal("hello world", record.Message);
        Assert.Equal(FixedNow, record.Time);
    }

    [Fact]
    public void Log_AppendsAcrossWrites()
    {
        var sink = new RecordingLogRecordSink(new FakeTimeProvider(FixedNow));
        var logger = new FileLogger("Mohist.Server", sink);

        logger.LogInformation("first");
        logger.LogInformation("second");

        Assert.Equal(["first", "second"], sink.Records.Select(record => record.Message));
    }

    [Fact]
    public void Log_IncludesExceptionWhenProvided()
    {
        var sink = new RecordingLogRecordSink(new FakeTimeProvider(FixedNow));
        var logger = new FileLogger("Mohist.Server", sink);

        logger.LogError(new InvalidOperationException("boom"), "operation failed");

        var record = Assert.Single(sink.Records);
        Assert.Equal("ERROR", record.Level);
        Assert.Equal("operation failed", record.Message);
        Assert.Contains("boom", record.Exception);
    }

    [Theory]
    [InlineData("Mohist.Server.Workflow.Services.Runner", "Mohist.Server")]
    [InlineData("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore")]
    [InlineData("Mohist.Server", "Mohist.Server")]
    [InlineData("Mohist", "Mohist")]
    public void Log_ProjectsStableServiceName(string category, string expectedService)
    {
        var sink = new RecordingLogRecordSink(new FakeTimeProvider(FixedNow));
        var logger = new FileLogger(category, sink);

        logger.LogInformation("x");

        Assert.Equal(expectedService, Assert.Single(sink.Records).Service);
    }

    [Fact]
    public void IsEnabled_UsesSinkPolicy()
    {
        var sink = new RecordingLogRecordSink(new FakeTimeProvider(FixedNow), LogLevel.Warning);
        var logger = new FileLogger("Mohist.Server", sink);

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.False(logger.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void LogRecord_FormatMatchesSharedJsonOptions()
    {
        var sink = new RecordingLogRecordSink(new FakeTimeProvider(FixedNow));
        var logger = new FileLogger("Mohist.Server.Workflow", sink);
        logger.LogInformation("hello {name}", "world");

        var line = JsonSerializer.Serialize(Assert.Single(sink.Records), JSON.Options);
        var record = JsonSerializer.Deserialize<LogRecord>(line, JSON.Options);

        Assert.NotNull(record);
        Assert.Equal("INFO", record!.Level);
        Assert.Equal("Mohist.Server", record.Service);
        Assert.Equal("hello world", record.Message);
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
