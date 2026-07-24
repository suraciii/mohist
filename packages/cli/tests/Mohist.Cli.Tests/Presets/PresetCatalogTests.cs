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

    private static FakeFileSystem CreateFiles()
    {
        var files = new FakeFileSystem();
        files.CreateDirectory("/assets/presets");
        files.CreateDirectory("/assets/presets/supervisor");
        files.AddFile("/assets/presets/manifest.json", """
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
        files.AddFile("/assets/presets/supervisor/instructions.md", "identity");
        files.AddFile("/assets/presets/supervisor/approval.md", "Issue #{{event.issue}}");
        files.AddFile("/assets/presets/supervisor/failure.md", "Run {{event.workflowrunid}}");
        return files;
    }
}
