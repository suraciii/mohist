using Mohist.Cli.Tests.Support;
using Mohist.Cli;
using EnvironmentAbstractions.TestHelpers;
using Xunit;

namespace Mohist.Cli.Tests.Presets;

public sealed class PresetCatalogTests
{
    [Fact]
    public void ResolveSupervisorReadsTextAndMetadataWithoutRenderingPlaceholders()
    {
        var files = CreateFiles();
        var catalog = new PresetCatalog(files, "/assets/presets");

        var result = catalog.Resolve("supervisor");

        Assert.True(result.Found, result.Error);
        Assert.NotNull(result.Preset);
        Assert.NotEmpty(result.Preset!.Instructions);
        Assert.Contains("{{event.issue}}", result.Preset.Rules[0].ResponsePrompt, StringComparison.Ordinal);
        Assert.Contains("{{event.workflowrunid}}", result.Preset.Rules[1].ResponsePrompt, StringComparison.Ordinal);
        Assert.Equal("supervisor-approval", result.Preset.Rules[0].Name);
        Assert.Equal("event.type == \"com.mohist.workflow.stage.approval-requested\"", result.Preset.Rules[0].Match);
        Assert.Equal("supervisor-failure", result.Preset.Rules[1].Name);
        Assert.Equal("event.type == \"com.mohist.workflow.run.failed\"", result.Preset.Rules[1].Match);
    }

    [Fact]
    public void UnknownPresetListsAvailableNames()
    {
        var catalog = new PresetCatalog(CreateFiles(), "/assets/presets");

        var result = catalog.Resolve("acme");

        Assert.False(result.Found);
        Assert.Contains("supervisor", result.AvailableNames);
        Assert.Contains("supervisor", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ListNamesReturnsManifestNames()
    {
        var catalog = new PresetCatalog(CreateFiles(), "/assets/presets");

        Assert.Equal(new[] { "supervisor" }, catalog.ListNames());
    }

    [Fact]
    public void CreateDefault_ResolvesManagedCacheLayout_AndFindsSupervisor()
    {
        // Mirrors the post-`mo update` steady state: presets live under
        // <home>/.mohist/cli/presets (managed cache), independent of skill-data.
        var fs = new FakeFileSystem();
        SeedManagedPresets(fs, home: "/home/test");

        var catalog = PresetCatalog.CreateDefault(fs, () => "/home/test");

        var result = catalog.Resolve("supervisor");
        Assert.True(result.Found, result.Error);
        Assert.NotNull(result.Preset);
        Assert.Equal("supervisor", result.Preset!.Name);
        Assert.Equal("identity", result.Preset.Instructions);
    }

    [Fact]
    public void CreateDefault_WhenManagedCacheMissing_FallsBackToSiblingDirectory()
    {
        // Dev/source-build case: no managed cache, presets sit next to the
        // binary at the sibling root the resolver is told about.
        var fs = new FakeFileSystem();
        var sibling = "/app/presets";
        SeedPresetsAt(fs, sibling);

        var catalog = new PresetCatalogFactory(fs, () => "/home/empty", sibling).Create();

        var result = catalog.Resolve("supervisor");
        Assert.True(result.Found, result.Error);
        Assert.Equal("supervisor", result.Preset!.Name);
    }

    [Fact]
    public void CreateDefault_WhenPresetsAbsentEverywhere_ListsNoPresets()
    {
        // The failure mode F1 reported: managed skill-data present but presets
        // missing. The catalog must report an empty catalog (unknown preset)
        // rather than throwing, so install surfaces a clean error.
        var fs = new FakeFileSystem();
        fs.CreateDirectory("/home/test/.mohist/cli/skill-data");

        var catalog = PresetCatalog.CreateDefault(fs, () => "/home/test");

        Assert.Empty(catalog.ListNames());
        var result = catalog.Resolve("supervisor");
        Assert.False(result.Found);
        Assert.Empty(result.AvailableNames);
    }

    internal static void SeedManagedPresets(FakeFileSystem fs, string home) =>
        SeedPresetsAt(fs, Path.Combine(home, ".mohist", "cli", "presets"));

    internal static void SeedPresetsAt(FakeFileSystem fs, string root)
    {
        fs.CreateDirectory(root);
        fs.CreateDirectory(Path.Combine(root, "supervisor"));
        fs.AddFile(Path.Combine(root, "manifest.json"), """
            {
              "supervisor": {
                "instructions": "supervisor/instructions.md",
                "rules": [
                  { "name": "supervisor-approval", "match": "event.type == \"com.mohist.workflow.stage.approval-requested\"", "responsePrompt": "supervisor/approval.md" },
                  { "name": "supervisor-failure", "match": "event.type == \"com.mohist.workflow.run.failed\"", "responsePrompt": "supervisor/failure.md" }
                ]
              }
            }
            """);
        fs.AddFile(Path.Combine(root, "supervisor", "instructions.md"), "identity");
        fs.AddFile(Path.Combine(root, "supervisor", "approval.md"), "Issue #{{event.issue}}");
        fs.AddFile(Path.Combine(root, "supervisor", "failure.md"), "Run {{event.workflowrunid}}");
    }

    // Lets the sibling-fallback test point the resolver at a chosen sibling
    // root without depending on AppContext.BaseDirectory.
    internal sealed class PresetCatalogFactory(IFileSystem fs, Func<string?> home, string sibling)
    {
        public PresetCatalog Create() => new(
            fs,
            new PresetAssetRootResolver(fs, home, () => sibling).Resolve());
    }

    private static FakeFileSystem CreateFiles()
    {
        var files = new FakeFileSystem();
        SeedPresetsAt(files, "/assets/presets");
        return files;
    }
}

