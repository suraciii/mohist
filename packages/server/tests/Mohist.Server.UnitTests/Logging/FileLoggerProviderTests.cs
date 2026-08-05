using System.Text;
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

// Banned for tests of other components; this is the provider's own test and writes only to the in-memory file system.
#pragma warning disable RS0030
public class FileLoggerProviderRotationTests
{
    private const string LogPath = "/logs/server.log";

    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 30, 12, 34, 56, 789, TimeSpan.FromHours(8));

    [Fact]
    public void WriteRecord_RotationKeepsCurrentPlusTwoGenerations()
    {
        var fileSystem = new InMemoryLogFileSystem();
        var logger = CreateLogger(fileSystem, maxLogFileBytes: 120, out var provider);

        for (var index = 1; index <= 23; index++)
        {
            logger.LogInformation("line-{Number:000}", index);
        }
        provider.Dispose();

        Assert.True(fileSystem.Exists(LogPath));
        Assert.True(fileSystem.Exists(LogPath + ".1"));
        Assert.True(fileSystem.Exists(LogPath + ".2"));
        Assert.False(fileSystem.Exists(LogPath + ".3"));

        Assert.Equal(["line-023"], MessagesIn(fileSystem.ReadText(LogPath)!));
        Assert.Equal(["line-022"], MessagesIn(fileSystem.ReadText(LogPath + ".1")!));
        Assert.Equal(["line-021"], MessagesIn(fileSystem.ReadText(LogPath + ".2")!));
    }

    [Fact]
    public void WriteRecord_RotationKeepsEveryLineParseable()
    {
        var fileSystem = new InMemoryLogFileSystem();
        var logger = CreateLogger(fileSystem, maxLogFileBytes: 250, out var provider);

        for (var index = 1; index <= 12; index++)
        {
            logger.LogInformation("line-{Number:000}", index);
        }
        provider.Dispose();

        var lines = new[] { LogPath + ".2", LogPath + ".1", LogPath }
            .SelectMany(path => fileSystem.ReadText(path)!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        Assert.Equal(6, lines.Length);
        Assert.All(lines, line => Assert.True(Logfmt.TryParse(line, out _)));
        Assert.Equal(
            ["line-007", "line-008", "line-009", "line-010", "line-011", "line-012"],
            lines.Select(line => { Logfmt.TryParse(line, out var values); return values["msg"]; }));
    }

    private static ILogger CreateLogger(
        InMemoryLogFileSystem fileSystem,
        long maxLogFileBytes,
        out FileLoggerProvider provider)
    {
        provider = new FileLoggerProvider(
            new StubLogPathResolver("/logs"),
            new FakeTimeProvider(FixedNow),
            LogLevel.Information,
            fileSystem,
            maxLogFileBytes);
        return provider.CreateLogger("Mohist.Server.Runner.Services.DispatchService");
    }

    private static string[] MessagesIn(string content)
        => content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Logfmt.TryParse(line, out var values)
                ? values["msg"]
                : throw new InvalidOperationException($"unparseable line: {line}"))
            .ToArray();

    private sealed class StubLogPathResolver(string path) : ILogPathResolver
    {
        public string Resolve() => path;
    }

    private sealed class InMemoryLogFileSystem : ILogFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public bool Exists(string path) => _files.ContainsKey(path);

        public void CreateDirectory(string path) { }

        public void Delete(string path) => _files.Remove(path);

        public void Move(string source, string destination)
        {
            _files[destination] = _files[source];
            _files.Remove(source);
        }

        public Stream OpenAppend(string path)
            => new CommitOnDisposeStream(this, path, _files.TryGetValue(path, out var existing) ? existing : []);

        public string? ReadText(string path)
            => _files.TryGetValue(path, out var data) ? Encoding.UTF8.GetString(data) : null;

        private void Commit(string path, byte[] content) => _files[path] = content;

        private sealed class CommitOnDisposeStream : MemoryStream
        {
            private readonly InMemoryLogFileSystem _fileSystem;
            private readonly string _path;

            public CommitOnDisposeStream(InMemoryLogFileSystem fileSystem, string path, byte[] initial)
            {
                _fileSystem = fileSystem;
                _path = path;
                Write(initial, 0, initial.Length);
                Position = Length;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _fileSystem.Commit(_path, ToArray());
                }
                base.Dispose(disposing);
            }
        }
    }
}
#pragma warning restore RS0030
