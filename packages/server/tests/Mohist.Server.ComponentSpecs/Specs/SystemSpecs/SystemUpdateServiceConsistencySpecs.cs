using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.ComponentSpecs.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;
using static Mohist.Server.ComponentSpecs.Specs.SystemSpecs.SystemUpdateServiceTestSupport;

namespace Mohist.Server.ComponentSpecs.Specs.SystemSpecs;

public class SystemUpdateServiceConsistencySpecs
{
    [Fact]
    public async Task GetConsistencyAsync_AllCoherentReturnsConsistent()
    {
        var store = new InMemoryUpdateStore();
        var skillDataDir = Path.Combine(Path.GetTempPath(), $"mohist-consistency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(skillDataDir);
        Directory.CreateDirectory(Path.Combine(skillDataDir, "mohist"));
        File.WriteAllText(Path.Combine(skillDataDir, "mohist", "SKILL.md"), "---\nname: mohist\ndescription: test.\n---\n\n# mohist\n");
        try
        {
            var service = CreateConsistencyService(
                new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
                store,
                new RecordingCommandRunner(),
                new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
                skillDataDir);

            var response = await service.GetConsistencyAsync();

            Assert.Equal("consistent", response.Status);
            Assert.All(response.Components, component => Assert.Equal("consistent", component.Status));
        }
        finally
        {
            Directory.Delete(skillDataDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetConsistencyAsync_RunnerUnavailableIsReported()
    {
        var store = new InMemoryUpdateStore();
        var skillDataDir = Path.Combine(Path.GetTempPath(), $"mohist-consistency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(skillDataDir);
        Directory.CreateDirectory(Path.Combine(skillDataDir, "mohist"));
        File.WriteAllText(Path.Combine(skillDataDir, "mohist", "SKILL.md"), "---\nname: mohist\ndescription: test.\n---\n\n# mohist\n");
        try
        {
            var service = CreateConsistencyService(
                new SequencedSystemInfo(CreateInfo(
                    runningGitHash: "newhash",
                    sourceHead: "newhash",
                    serverServiceStatus: "active",
                    runnerServiceStatus: "inactive")),
                store,
                new RecordingCommandRunner(),
                new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
                skillDataDir);

            var response = await service.GetConsistencyAsync();

            Assert.Equal("inconsistent", response.Status);
            var runner = Assert.Single(response.Components, c => c.Name == "runner");
            Assert.Equal("unavailable", runner.Status);
        }
        finally
        {
            Directory.Delete(skillDataDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetConsistencyAsync_ManagedAssetsMismatchedWhenSkillFilesMissing()
    {
        var store = new InMemoryUpdateStore();
        var missingDir = Path.Combine(Path.GetTempPath(), $"mohist-consistency-{Guid.NewGuid():N}");
        try
        {
            var service = CreateConsistencyService(
                new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
                store,
                new RecordingCommandRunner(),
                new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
                missingDir);

            var response = await service.GetConsistencyAsync();

            Assert.Equal("inconsistent", response.Status);
            var managed = Assert.Single(response.Components, c => c.Name == "managed-assets");
            Assert.Equal("mismatched", managed.Status);
        }
        finally
        {
            if (Directory.Exists(missingDir))
                Directory.Delete(missingDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetConsistencyAsync_ManagedAssetsMismatchedWhenSkillDataDirMissing()
    {
        var store = new InMemoryUpdateStore();
        var skillDataDir = Path.Combine(Path.GetTempPath(), $"mohist-consistency-{Guid.NewGuid():N}");
        try
        {
            var service = CreateConsistencyService(
                new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
                store,
                new RecordingCommandRunner(),
                new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
                skillDataDir);

            var response = await service.GetConsistencyAsync();

            Assert.Equal("inconsistent", response.Status);
            var managed = Assert.Single(response.Components, c => c.Name == "managed-assets");
            Assert.Equal("mismatched", managed.Status);
        }
        finally
        {
            if (Directory.Exists(skillDataDir))
                Directory.Delete(skillDataDir, recursive: true);
        }
    }
}
