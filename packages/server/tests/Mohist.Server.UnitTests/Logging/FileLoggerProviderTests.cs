using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Logging;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.UnitTests.Logging;

public class FileLoggerProviderTests
{
    [Fact]
    public void AddFileLogger_RegistersOneProviderSharedWithILoggerProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<ILogPathResolver>(_ => CreateResolver("/logs"));
        services.AddLogging(builder => builder.AddFileLogger());
        services.AddMohistServerCore(new ConfigurationBuilder().Build());

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(FileLoggerProvider));

        using var provider = services.BuildServiceProvider();
        var concrete = provider.GetRequiredService<FileLoggerProvider>();
        var loggingProvider = provider.GetServices<ILoggerProvider>()
            .OfType<FileLoggerProvider>()
            .Single();

        Assert.Same(concrete, loggingProvider);
    }

    [Fact]
    public void WriteRecord_AppendsJsonObjectWithLevelTimeServiceMessageToFile()
    {
        var now = new DateTimeOffset(2026, 6, 30, 12, 34, 56, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        using var provider = CreateProvider("/test/logs", time, out var sink);
        var logger = provider.CreateLogger("Mohist.Server.Workflow.Grains");
        logger.LogInformation("hello world");

        var lines = sink.Lines;
        Assert.Single(lines);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;

        Assert.Equal("INFO", root.GetProperty("level").GetString());
        Assert.Equal("Mohist.Server", root.GetProperty("service").GetString());
        Assert.Equal("hello world", root.GetProperty("message").GetString());

        var parsedTime = root.GetProperty("time").GetDateTimeOffset();
        Assert.Equal(now.UtcDateTime, parsedTime.UtcDateTime);
    }

    [Fact]
    public void WriteRecord_OpensSinkOnFirstRecord()
    {
        using var provider = CreateProvider("/test/logs", out var sink);
        var logger = provider.CreateLogger("Mohist.Server");
        logger.LogWarning("started up");

        Assert.Equal([provider.LogFilePath], sink.OpenedPaths);
        Assert.Single(sink.Lines);
    }

    [Fact]
    public void WriteRecord_AppendsAcrossWrites()
    {
        using var provider = CreateProvider("/test/logs", out var sink);
        var logger = provider.CreateLogger("Mohist.Server");

        logger.LogInformation("first");
        logger.LogInformation("second");

        Assert.Equal(2, sink.Lines.Count);
        Assert.Contains("first", sink.Lines[0]);
        Assert.Contains("second", sink.Lines[1]);
    }

    [Fact]
    public void WriteRecord_IncludesExceptionWhenProvided()
    {
        using var provider = CreateProvider("/test/logs", out var sink);
        var logger = provider.CreateLogger("Mohist.Server");

        var ex = new InvalidOperationException("boom");
        logger.LogError(ex, "operation failed");

        var lines = sink.Lines;
        Assert.Single(lines);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;

        Assert.Equal("ERROR", root.GetProperty("level").GetString());
        Assert.Equal("operation failed", root.GetProperty("message").GetString());
        Assert.Contains("boom", root.GetProperty("exception").GetString()!);
    }

    [Fact]
    public void WriteRecord_UsesLoggerCategoryTopSegmentAsService()
    {
        using var provider = CreateProvider("/test/logs", out var sink);
        var logger = provider.CreateLogger("Mohist.Server.Workflow.Services.Runner");
        logger.LogInformation("x");

        using var doc = JsonDocument.Parse(Assert.Single(sink.Lines));
        Assert.Equal("Mohist.Server", doc.RootElement.GetProperty("service").GetString());
    }

    [Fact]
    public void IsEnabled_DisablesLogLevelNone()
    {
        using var provider = CreateProvider("/test/logs", out _);

        Assert.False(provider.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void WriteRecord_FormatMatchesJsonOptions_SoApiPipelineSerializesItIdentically()
    {
        using var provider = CreateProvider("/test/logs", out var sink);
        var logger = provider.CreateLogger("Mohist.Server.Workflow");
        logger.LogInformation("hello {name}", "world");

        var lines = sink.Lines;
        Assert.Single(lines);

        var record = JsonSerializer.Deserialize<LogRecord>(lines[0], JSON.Options);
        Assert.NotNull(record);
        Assert.Equal("INFO", record!.Level);
        Assert.Equal("Mohist.Server", record.Service);
        Assert.Equal("hello world", record.Message);
    }

    private static FileLoggerProvider CreateProvider(string dir, out RecordingLogFileSinkFactory sink)
    {
        return CreateProvider(
            dir,
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
            out sink);
    }

    private static FileLoggerProvider CreateProvider(string dir, TimeProvider time, out RecordingLogFileSinkFactory sink)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LogPathResolver.ConfigurationKey] = dir,
            })
            .Build();
        var resolver = new LogPathResolver(configuration, new MockEnvironmentVariableProvider());
        sink = new RecordingLogFileSinkFactory();
        return new FileLoggerProvider(resolver, time, LogLevel.Information, sink);
    }

    private static ILogPathResolver CreateResolver(string dir)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LogPathResolver.ConfigurationKey] = dir,
            })
            .Build();
        return new LogPathResolver(configuration, new MockEnvironmentVariableProvider());
    }

    private sealed class RecordingLogFileSinkFactory : ILogFileSinkFactory
    {
        public List<string> OpenedPaths { get; } = [];
        public List<string> Lines { get; } = [];

        public ILogFileSink Open(string path)
        {
            OpenedPaths.Add(path);
            return new RecordingLogFileSink(Lines);
        }
    }

    private sealed class RecordingLogFileSink(List<string> lines) : ILogFileSink
    {
        public void WriteLine(string line) => lines.Add(line);

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
