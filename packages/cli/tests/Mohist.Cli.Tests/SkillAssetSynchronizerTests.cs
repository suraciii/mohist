using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class SkillAssetSynchronizerTests
{
    private const string SourceDir = "/repo/packages/cli/Mohist.Cli/skill-data";
    private const string ManagedDir = "/home/test/.mohist/cli/skill-data";

    private static (SkillAssetSynchronizer sut, StringWriter output, StringWriter error, FakeFileSystem fs)
        BuildSut()
    {
        var fs = new FakeFileSystem();
        var output = new StringWriter();
        var error = new StringWriter();
        return (new SkillAssetSynchronizer(output, error, fs), output, error, fs);
    }

    private static void RegisterDirectory(FakeFileSystem fs, params string[] paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var current = path;
            while (!string.IsNullOrEmpty(current))
            {
                if (seen.Add(current))
                    fs.CreateDirectory(current);
                var parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.Ordinal)) break;
                current = parent ?? string.Empty;
            }
        }
    }

    [Fact]
    public async Task SyncAsync_PreparedCacheContainsSourceSkillFiles_LeavesManagedDirByteForByteEqualToSource()
    {
        var (sut, output, error, fs) = BuildSut();
        var mohistSource = "---\nname: mohist\ndescription: dispatcher\n---\n# mohist\n";
        var epicSource = "---\nname: mohist-create-epic\ndescription: autopilot lifecycle\n---\n# mohist-create-epic\n";

        fs.AddFile(Path.Combine(SourceDir, "manifest.json"), "{\"skills\":[\"mohist\",\"mohist-create-epic\"]}");
        fs.AddFile(Path.Combine(SourceDir, "mohist", "SKILL.md"), mohistSource);
        fs.AddFile(Path.Combine(SourceDir, "mohist-create-epic", "SKILL.md"), epicSource);
        RegisterDirectory(fs, SourceDir, Path.Combine(SourceDir, "mohist"), Path.Combine(SourceDir, "mohist-create-epic"));

        var exitCode = await sut.SyncAsync(SourceDir, ManagedDir);

        Assert.True(exitCode == 0, $"expected 0, got {exitCode}, stderr='{error}'");
        Assert.True(fs.DirectoryExists(ManagedDir));

        Assert.Equal(mohistSource, fs.ReadAllText(Path.Combine(ManagedDir, "mohist", "SKILL.md")));
        Assert.Equal(epicSource, fs.ReadAllText(Path.Combine(ManagedDir, "mohist-create-epic", "SKILL.md")));
        Assert.Equal("{\"skills\":[\"mohist\",\"mohist-create-epic\"]}",
            fs.ReadAllText(Path.Combine(ManagedDir, "manifest.json")));

        Assert.Contains("Synchronized managed skill assets", output.ToString());
        Assert.DoesNotContain("error", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncAsync_StaleManagedCacheIsReplacedBySourceContents()
    {
        var (sut, _, error, fs) = BuildSut();
        const string staleDispatcher = "STALE_DISPATCHER_CONTENT";
        const string freshDispatcher = "---\nname: mohist\ndescription: dispatcher\n---\n# mohist\nfresh\n";

        fs.AddFile(Path.Combine(SourceDir, "mohist", "SKILL.md"), freshDispatcher);
        fs.AddFile(Path.Combine(ManagedDir, "mohist", "SKILL.md"), staleDispatcher);
        RegisterDirectory(fs, SourceDir, Path.Combine(SourceDir, "mohist"), ManagedDir, Path.Combine(ManagedDir, "mohist"));

        var exitCode = await sut.SyncAsync(SourceDir, ManagedDir);

        Assert.True(exitCode == 0, $"expected 0, got {exitCode}, stderr='{error}'");
        Assert.Equal(freshDispatcher, fs.ReadAllText(Path.Combine(ManagedDir, "mohist", "SKILL.md")));
        Assert.DoesNotContain(staleDispatcher, fs.ReadAllText(Path.Combine(ManagedDir, "mohist", "SKILL.md")));
    }

    [Fact]
    public async Task SyncAsync_SourceMissing_ReturnsErrorAndLeavesManagedDirUnchanged()
    {
        var (sut, _, error, fs) = BuildSut();
        var existingCacheContent = "---\nname: mohist\ndescription: cached\n---";
        fs.AddFile(Path.Combine(ManagedDir, "mohist", "SKILL.md"), existingCacheContent);
        RegisterDirectory(fs, ManagedDir, Path.Combine(ManagedDir, "mohist"));

        var exitCode = await sut.SyncAsync(SourceDir, ManagedDir);

        Assert.Equal(1, exitCode);
        Assert.Contains("source skill-data directory", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existingCacheContent, fs.ReadAllText(Path.Combine(ManagedDir, "mohist", "SKILL.md")));
    }

    [Fact]
    public async Task SyncAsync_ManagedDirEmpty_ReturnsSuccessAndPopulatesFromSource()
    {
        var (sut, output, error, fs) = BuildSut();
        const string dispatcherSource = "---\nname: mohist\ndescription: dispatcher\n---";

        fs.AddFile(Path.Combine(SourceDir, "mohist", "SKILL.md"), dispatcherSource);
        RegisterDirectory(fs, SourceDir, Path.Combine(SourceDir, "mohist"));

        var exitCode = await sut.SyncAsync(SourceDir, ManagedDir);

        Assert.True(exitCode == 0, $"expected 0, got {exitCode}, stderr='{error}'");
        Assert.Equal(dispatcherSource, fs.ReadAllText(Path.Combine(ManagedDir, "mohist", "SKILL.md")));
        Assert.Contains(ManagedDir, output.ToString());
    }

    [Fact]
    public async Task SyncAsync_SkillServiceReadsFromManagedCache_OutputMatchesEditedSourceByteForByte()
    {
        var (sut, _, error, fs) = BuildSut();

        var dispatcherSource =
            "---\n" +
            "name: mohist\n" +
            "description: dispatcher\n" +
            "---\n" +
            "# mohist\n" +
            "issue lifecycle: mo issue start rebase close; run control: mo run approve reject retry rerun pause resume stop\n" +
            "epic autopilot: mo epic start pause resume\n";

        var epicSource =
            "---\n" +
            "name: mohist-create-epic\n" +
            "description: autopilot lifecycle\n" +
            "---\n" +
            "# mohist-create-epic\n" +
            "Prefer `mo epic start` autopilot over manual per-issue starts.\n" +
            "`mo epic start` is idempotent: starting an already-running epic is a no-op.\n" +
            "running-but-idle: all member issues complete, awaiting the next.\n";

        fs.AddFile(Path.Combine(SourceDir, "mohist", "SKILL.md"), dispatcherSource);
        fs.AddFile(Path.Combine(SourceDir, "mohist-create-epic", "SKILL.md"), epicSource);
        RegisterDirectory(fs, SourceDir, Path.Combine(SourceDir, "mohist"), Path.Combine(SourceDir, "mohist-create-epic"));

        var exitCode = await sut.SyncAsync(SourceDir, ManagedDir);
        Assert.True(exitCode == 0, $"sync failed: {error}");

        var service = new SkillAssetService(fs, overrideAssetRoot: ManagedDir);

        var dispatcherRead = service.GetSkill("mohist", includeSupplementaryFiles: false);
        var epicRead = service.GetSkill("mohist-create-epic", includeSupplementaryFiles: false);

        Assert.True(dispatcherRead.Found, $"dispatcher should be discoverable post-sync: {dispatcherRead.Error}");
        Assert.True(epicRead.Found, $"epic should be discoverable post-sync: {epicRead.Error}");

        Assert.Equal(dispatcherSource, dispatcherRead.Skill!.SkillMarkdown);
        Assert.Equal(epicSource, epicRead.Skill!.SkillMarkdown);
    }
}
