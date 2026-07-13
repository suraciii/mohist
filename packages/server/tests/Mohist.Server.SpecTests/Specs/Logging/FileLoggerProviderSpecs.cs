using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Logging;
using Mohist.Server.SpecTests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.SpecTests.Specs.Logging;

public class FileLoggerProviderSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
