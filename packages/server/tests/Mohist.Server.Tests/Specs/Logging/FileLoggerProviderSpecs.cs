using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Logging;
using Mohist.Server.Tests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Specs.Logging;

public class FileLoggerProviderSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void WriteRecord_AppendsJsonObjectWithLevelTimeServiceMessageToFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mohist-logger-{Guid.NewGuid():N}");
        var now = new DateTimeOffset(2026, 6, 30, 12, 34, 56, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);

        try
        {
            using var provider = CreateProvider(dir, time, out _);
            var logger = provider.CreateLogger("Mohist.Server.Workflow.Grains");
            logger.LogInformation("hello world");

            var lines = ReadAllLines(provider.LogFilePath);
            Assert.Single(lines);

            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;

            Assert.Equal("INFO", root.GetProperty("level").GetString());
            Assert.Equal("Mohist.Server", root.GetProperty("service").GetString());
            Assert.Equal("hello world", root.GetProperty("message").GetString());

            var parsedTime = root.GetProperty("time").GetDateTimeOffset();
            Assert.Equal(now.UtcDateTime, parsedTime.UtcDateTime);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void WriteRecord_WhenDirectoryMissing_CreatesDirectoryBeforeFirstRecord()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mohist-logger-{Guid.NewGuid():N}");
        var nested = Path.Combine(dir, "logs");
        Assert.False(Directory.Exists(nested));

        try
        {
            using var provider = CreateProvider(nested, out _);
            var logger = provider.CreateLogger("Mohist.Server");
            logger.LogWarning("started up");

            Assert.True(Directory.Exists(nested));
            Assert.True(File.Exists(provider.LogFilePath));

            var lines = ReadAllLines(provider.LogFilePath);
            Assert.Single(lines);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void WriteRecord_AppendsAcrossWrites_AndFileRemainsConcurrentlyReadable()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mohist-logger-{Guid.NewGuid():N}");
        try
        {
            using var provider = CreateProvider(dir, out _);
            var logger = provider.CreateLogger("Mohist.Server");

            logger.LogInformation("first");
            using (var reader = new FileStream(provider.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(reader))
            {
                var seen = sr.ReadToEnd();
                Assert.Contains("first", seen);
            }

            logger.LogInformation("second");
            using (var reader = new FileStream(provider.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(reader))
            {
                var seen = sr.ReadToEnd();
                Assert.Contains("first", seen);
                Assert.Contains("second", seen);
            }

            var lines = ReadAllLines(provider.LogFilePath);
            Assert.Equal(2, lines.Length);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void WriteRecord_IncludesExceptionWhenProvided()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mohist-logger-{Guid.NewGuid():N}");
        try
        {
            using var provider = CreateProvider(dir, out _);
            var logger = provider.CreateLogger("Mohist.Server");

            var ex = new InvalidOperationException("boom");
            logger.LogError(ex, "operation failed");

            var lines = ReadAllLines(provider.LogFilePath);
            Assert.Single(lines);

            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;

            Assert.Equal("ERROR", root.GetProperty("level").GetString());
            Assert.Equal("operation failed", root.GetProperty("message").GetString());
            Assert.Contains("boom", root.GetProperty("exception").GetString()!);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void WriteRecord_UsesLoggerCategoryTopSegmentAsService()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mohist-logger-{Guid.NewGuid():N}");
        try
        {
            using var provider = CreateProvider(dir, out _);
            var logger = provider.CreateLogger("Mohist.Server.Workflow.Services.Runner");
            logger.LogInformation("x");

            var lines = ReadAllLines(provider.LogFilePath);
            using var doc = JsonDocument.Parse(lines[0]);
            Assert.Equal("Mohist.Server", doc.RootElement.GetProperty("service").GetString());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void WriteRecord_FormatMatchesJsonOptions_SoApiPipelineSerializesItIdentically()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mohist-logger-{Guid.NewGuid():N}");
        try
        {
            using var provider = CreateProvider(dir, out _);
            var logger = provider.CreateLogger("Mohist.Server.Workflow");
            logger.LogInformation("hello {name}", "world");

            var lines = ReadAllLines(provider.LogFilePath);
            Assert.Single(lines);

            var record = JsonSerializer.Deserialize<LogRecord>(lines[0], JSON.Options);
            Assert.NotNull(record);
            Assert.Equal("INFO", record!.Level);
            Assert.Equal("Mohist.Server", record.Service);
            Assert.Equal("hello world", record.Message);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void AddFileLogger_RegistersOneProviderInstanceSharedWithILoggerProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mohist-logger-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<ILogPathResolver>(_ => CreateResolver(dir));
            services.AddLogging(builder => builder.AddFileLogger());
            services.AddMohistServerCore(new ConfigurationBuilder().Build());

            Assert.Single(services, d => d.ServiceType == typeof(FileLoggerProvider));

            using var provider = services.BuildServiceProvider();
            var concrete = provider.GetRequiredService<FileLoggerProvider>();
            var loggingProvider = provider.GetServices<ILoggerProvider>()
                .OfType<FileLoggerProvider>()
                .Single();

            Assert.Same(concrete, loggingProvider);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static FileLoggerProvider CreateProvider(string dir, out ILogPathResolver resolver)
    {
        return CreateProvider(dir, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)), out resolver);
    }

    private static FileLoggerProvider CreateProvider(string dir, TimeProvider time, out ILogPathResolver resolver)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LogPathResolver.ConfigurationKey] = dir,
            })
            .Build();
        resolver = new LogPathResolver(configuration, new MockEnvironmentVariableProvider());
        return new FileLoggerProvider(resolver, time);
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

    private static string[] ReadAllLines(string path)
        => File.ReadAllLines(path, Encoding.UTF8);

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
