using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

[Collection("ConsoleOutput")]
public class MohistConfigurationExtensionsTests
{
    private readonly InMemoryFileProvider _files = new();

    private string CreateJsonc(string content)
    {
        const string path = "config.jsonc";
        _files.SetFile(path, content);
        return path;
    }

    private IConfigurationBuilder CreateBuilder() => new ConfigurationBuilder().SetFileProvider(_files);

    [Fact]
    public void AddMohistUserConfigFile_WhenEnvironmentIsTesting_DoesNotRegisterJsonSource()
    {
        var path = CreateJsonc("""{ "Mohist": { "Host": "from-user-file" } }""");
        var environment = new TestHostEnvironment(MohistHostEnvironment.Testing);

        var builder = CreateBuilder();
        builder.AddMohistUserConfigFile(environment, path: path, optional: true, reloadOnChange: true);
        var cfg = builder.Build();

        Assert.Empty(builder.Sources.OfType<JsonConfigurationSource>());
        Assert.Null(cfg["Mohist:Host"]);
    }

    [Fact]
    public void AddMohistUserConfigFile_WhenEnvironmentIsNotTesting_RegistersJsonSource()
    {
        var path = CreateJsonc("""{ "Mohist": { "Host": "from-user-file" } }""");
        var environment = new TestHostEnvironment(Environments.Production);

        var builder = CreateBuilder();
        builder.AddMohistUserConfigFile(environment, path: path, optional: true, reloadOnChange: false);
        var cfg = builder.Build();

        Assert.Single(builder.Sources.OfType<JsonConfigurationSource>());
        Assert.Equal("from-user-file", cfg["Mohist:Host"]);
    }

    [Fact]
    public void AddMohistConfigFile_RegistersJsonConfigurationSourceWithReloadOnChangeTrue()
    {
        var path = CreateJsonc("""{ "Mohist": { "Host": "h" } }""");

        var builder = CreateBuilder();
        builder.AddMohistConfigFile(path: path, optional: true, reloadOnChange: true);

        var jsonSource = builder.Sources.OfType<JsonConfigurationSource>().SingleOrDefault();
        Assert.NotNull(jsonSource);
        Assert.True(jsonSource.ReloadOnChange,
            "AddMohistConfigFile must wire reloadOnChange into the underlying JsonConfigurationSource");
        Assert.True(jsonSource.Optional);
    }

    [Fact]
    public void AddMohistConfigFile_JsoncWithLineAndBlockCommentsAndTrailingCommas_LoadsEveryConfiguredKey()
    {
        var path = CreateJsonc("""
            // top-level line comment
            {
              /* block comment before Mohist */
              "Mohist": {
                // nested line comment
                "WorkspaceCleanup": {
                  /* block comment between keys */
                  "RetentionDays": 30,
                  "StorageBudgetBytes": 1073741824,
                  "StorageTargetWatermarkBytes": 536870912, /* inline block, trailing comma follows */
                },
              },
            }
            """);

        var cfg = CreateBuilder()
            .AddMohistConfigFile(path: path, optional: true, reloadOnChange: false)
            .Build();

        Assert.Equal("30", cfg["Mohist:WorkspaceCleanup:RetentionDays"]);
        Assert.Equal("1073741824", cfg["Mohist:WorkspaceCleanup:StorageBudgetBytes"]);
        Assert.Equal("536870912", cfg["Mohist:WorkspaceCleanup:StorageTargetWatermarkBytes"]);
    }

    [Fact]
    public void AddMohistConfigFile_MissingFile_BuildsWithoutThrowing()
    {
        const string missing = "missing.jsonc";

        var cfg = CreateBuilder()
            .AddMohistConfigFile(path: missing, optional: true, reloadOnChange: false)
            .Build();

        Assert.Null(cfg["Mohist:Host"]);
    }

    [Fact]
    public void AddMohistConfigFile_MalformedFile_BuildsWithoutThrowingAndFallsBackToEmpty()
    {
        // Capture the OnLoadException warning we deliberately write to stderr so the
        // test output is not polluted by the expected warning under success.
        var originalErr = Console.Error;
        var capturedErr = new StringWriter();
        Console.SetError(capturedErr);
        try
        {
            var path = CreateJsonc("{ not valid jsonc");

            var builder = CreateBuilder();
            builder.AddMohistConfigFile(path: path, optional: true, reloadOnChange: false);
            var cfg = builder.Build();

            // Host starts; no exception escaped; defaults / other sources still queryable.
            Assert.Null(cfg["Mohist:Host"]);

            // The OnLoadException handler fired and logged to stderr.
            var stderr = capturedErr.ToString();
            Assert.Contains("[mohist-config]", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void AddMohistConfigFile_ReloadOnChangeFalse_PassesThroughToJsonSource()
    {
        var path = CreateJsonc("""{ "Mohist": { "Host": "h" } }""");

        var builder = CreateBuilder();
        builder.AddMohistConfigFile(path: path, optional: true, reloadOnChange: false);

        var jsonSource = builder.Sources.OfType<JsonConfigurationSource>().Single();
        Assert.False(jsonSource.ReloadOnChange);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Mohist.Server.UnitTests";
        public string ContentRootPath { get; set; } = "/test/content-root";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
