using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.SystemSpecs;

public class MohistConfigurationExtensionsSpecs : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
        }
    }

    private string CreateTempJsonc(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mohist-config-ext-{Guid.NewGuid():N}.jsonc");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void AddMohistConfigFile_RegistersJsonConfigurationSourceWithReloadOnChangeTrue()
    {
        var path = CreateTempJsonc("""{ "Mohist": { "Host": "h" } }""");

        var builder = new ConfigurationBuilder();
        builder.AddMohistConfigFile(path: path, optional: true, reloadOnChange: true);

        var jsonSource = builder.Sources.OfType<JsonConfigurationSource>().SingleOrDefault();
        Assert.NotNull(jsonSource);
        Assert.True(jsonSource.ReloadOnChange,
            "AddMohistConfigFile must wire reloadOnChange into the underlying JsonConfigurationSource");
        Assert.True(jsonSource.Optional);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void AddMohistConfigFile_JsoncWithLineAndBlockCommentsAndTrailingCommas_LoadsEveryConfiguredKey()
    {
        var path = CreateTempJsonc("""
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

        var cfg = new ConfigurationBuilder()
            .AddMohistConfigFile(path: path, optional: true, reloadOnChange: false)
            .Build();

        Assert.Equal("30", cfg["Mohist:WorkspaceCleanup:RetentionDays"]);
        Assert.Equal("1073741824", cfg["Mohist:WorkspaceCleanup:StorageBudgetBytes"]);
        Assert.Equal("536870912", cfg["Mohist:WorkspaceCleanup:StorageTargetWatermarkBytes"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void AddMohistConfigFile_MissingFile_BuildsWithoutThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"mohist-missing-{Guid.NewGuid():N}.jsonc");

        var cfg = new ConfigurationBuilder()
            .AddMohistConfigFile(path: missing, optional: true, reloadOnChange: false)
            .Build();

        Assert.Null(cfg["Mohist:Host"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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
            var path = CreateTempJsonc("{ not valid jsonc");

            var builder = new ConfigurationBuilder();
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void AddMohistConfigFile_ReloadOnChangeFalse_PassesThroughToJsonSource()
    {
        var path = CreateTempJsonc("""{ "Mohist": { "Host": "h" } }""");

        var builder = new ConfigurationBuilder();
        builder.AddMohistConfigFile(path: path, optional: true, reloadOnChange: false);

        var jsonSource = builder.Sources.OfType<JsonConfigurationSource>().Single();
        Assert.False(jsonSource.ReloadOnChange);
    }
}