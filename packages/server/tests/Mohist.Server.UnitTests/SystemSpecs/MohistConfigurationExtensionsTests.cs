using System.Text;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Hosting;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

[Collection("ConsoleCapture")]
public class MohistConfigurationExtensionsTests
{
    private const string ConfigPath = "/mohist-tests/config.jsonc";

    [Fact]
    public void AddMohistUserConfigFile_WhenEnvironmentIsTesting_DoesNotRegisterJsonSource()
    {
        var files = new InMemoryFileProvider().AddText(
            ConfigPath,
            """{ "Mohist": { "Host": "from-user-file" } }""");
        var environment = new TestHostEnvironment(MohistHostEnvironment.Testing);

        var builder = new ConfigurationBuilder();
        builder.AddMohistUserConfigFile(
            environment,
            ConfigPath,
            optional: true,
            reloadOnChange: true,
            files);
        var configuration = builder.Build();

        Assert.Empty(builder.Sources.OfType<JsonConfigurationSource>());
        Assert.Null(configuration["Mohist:Host"]);
    }

    [Fact]
    public void AddMohistUserConfigFile_WhenEnvironmentIsNotTesting_RegistersJsonSource()
    {
        var files = new InMemoryFileProvider().AddText(
            ConfigPath,
            """{ "Mohist": { "Host": "from-user-file" } }""");
        var environment = new TestHostEnvironment(Environments.Production);

        var builder = new ConfigurationBuilder();
        builder.AddMohistUserConfigFile(
            environment,
            ConfigPath,
            optional: true,
            reloadOnChange: false,
            files);
        var configuration = builder.Build();

        Assert.Single(builder.Sources.OfType<JsonConfigurationSource>());
        Assert.Equal("from-user-file", configuration["Mohist:Host"]);
    }

    [Fact]
    public void ConfigPath_WhenHomeIsProvided_IsSharedByStartupAndDocumentStore()
    {
        var environment = new MockEnvironmentVariableProvider();
        environment["HOME"] = "/mohist-tests/home";
        var expectedPath = "/mohist-tests/home/.mohist/config.jsonc";
        var files = new InMemoryFileProvider().AddText(expectedPath, "{}");
        var builder = new ConfigurationBuilder();

        builder.AddMohistConfigFile(
            path: null,
            optional: true,
            reloadOnChange: false,
            fileProvider: files,
            environment: environment);

        var source = Assert.Single(builder.Sources.OfType<JsonConfigurationSource>());
        Assert.Equal(expectedPath, source.Path);
        Assert.Equal(expectedPath, MohistConfigPath.Resolve(environment));
    }

    [Fact]
    public void AddMohistConfigFile_RegistersJsonConfigurationSourceWithReloadOnChangeTrue()
    {
        var files = new InMemoryFileProvider().AddText(ConfigPath, "{}");
        var builder = new ConfigurationBuilder();

        builder.AddMohistConfigFile(
            ConfigPath,
            optional: true,
            reloadOnChange: true,
            files);

        var source = Assert.Single(builder.Sources.OfType<JsonConfigurationSource>());
        Assert.True(source.ReloadOnChange);
        Assert.True(source.Optional);
        Assert.Same(files, source.FileProvider);
    }

    [Fact]
    public void ConfigurePhysicalConfigSource_ScopesProviderToConfigFile()
    {
        var source = new JsonConfigurationSource();
        var files = new InMemoryFileProvider();
        string? providerRoot = null;

        MohistConfigurationExtensions.ConfigurePhysicalConfigSource(
            source,
            ConfigPath,
            rootPath =>
            {
                providerRoot = rootPath;
                return files;
            });

        Assert.Equal("/mohist-tests", providerRoot);
        Assert.Equal("config.jsonc", source.Path);
        Assert.Same(files, source.FileProvider);
    }

    [Fact]
    public void AddMohistConfigFile_JsoncWithCommentsAndTrailingCommas_LoadsEveryConfiguredKey()
    {
        var files = new InMemoryFileProvider().AddText(ConfigPath, """
            // top-level line comment
            {
              /* block comment before Mohist */
              "Mohist": {
                "WorkspaceCleanup": {
                  "RetentionDays": 30,
                  "StorageBudgetBytes": 1073741824,
                  "StorageTargetWatermarkBytes": 536870912,
                },
              },
            }
            """);

        var configuration = new ConfigurationBuilder()
            .AddMohistConfigFile(
                ConfigPath,
                optional: true,
                reloadOnChange: false,
                files)
            .Build();

        Assert.Equal("30", configuration["Mohist:WorkspaceCleanup:RetentionDays"]);
        Assert.Equal("1073741824", configuration["Mohist:WorkspaceCleanup:StorageBudgetBytes"]);
        Assert.Equal("536870912", configuration["Mohist:WorkspaceCleanup:StorageTargetWatermarkBytes"]);
    }

    [Fact]
    public void AddMohistConfigFile_MissingFile_BuildsWithoutThrowing()
    {
        var configuration = new ConfigurationBuilder()
            .AddMohistConfigFile(
                ConfigPath,
                optional: true,
                reloadOnChange: false,
                new InMemoryFileProvider())
            .Build();

        Assert.Null(configuration["Mohist:Host"]);
    }

    [Fact]
    public void AddMohistConfigFile_MalformedFile_BuildsWithoutThrowingAndFallsBackToEmpty()
    {
        var originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);
        try
        {
            var files = new InMemoryFileProvider().AddText(ConfigPath, "{ not valid jsonc");
            var configuration = new ConfigurationBuilder()
                .AddMohistConfigFile(
                    ConfigPath,
                    optional: true,
                    reloadOnChange: false,
                    files)
                .Build();

            Assert.Null(configuration["Mohist:Host"]);
            Assert.Contains("[mohist-config]", capturedError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void AddMohistConfigFile_ReloadOnChangeFalse_PassesThroughToJsonSource()
    {
        var builder = new ConfigurationBuilder();

        builder.AddMohistConfigFile(
            ConfigPath,
            optional: true,
            reloadOnChange: false,
            new InMemoryFileProvider().AddText(ConfigPath, "{}"));

        Assert.False(Assert.Single(builder.Sources.OfType<JsonConfigurationSource>()).ReloadOnChange);
    }

    private sealed class InMemoryFileProvider : IFileProvider
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public InMemoryFileProvider AddText(string path, string content)
        {
            _files[Normalize(path)] = Encoding.UTF8.GetBytes(content);
            return this;
        }

        public IDirectoryContents GetDirectoryContents(string subpath) =>
            NotFoundDirectoryContents.Singleton;

        public IFileInfo GetFileInfo(string subpath)
        {
            var path = Normalize(subpath);
            return _files.TryGetValue(path, out var content)
                ? new InMemoryFileInfo(path, content)
                : new NotFoundFileInfo(path);
        }

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

        private static string Normalize(string path) => path.TrimStart('/');

        private sealed class InMemoryFileInfo(string name, byte[] content) : IFileInfo
        {
            public bool Exists => true;
            public long Length => content.LongLength;
            public string? PhysicalPath => null;
            public string Name => name;
            public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
            public bool IsDirectory => false;
            public Stream CreateReadStream() => new MemoryStream(content, writable: false);
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Mohist.Server.UnitTests";
        public string ContentRootPath { get; set; } = "/mohist-tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
