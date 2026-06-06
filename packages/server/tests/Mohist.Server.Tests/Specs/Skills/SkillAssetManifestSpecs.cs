using global::System.Text.Json;
using Mohist.Cli;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Skills;

public sealed class SkillAssetManifestSpecs : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mohist-skill-manifest-{Guid.NewGuid():N}");

    public SkillAssetManifestSpecs()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Write_ThenTryRead_RoundTripsVersionGitHashAndSkills()
    {
        var root = Path.Combine(_tempRoot, "roundtrip");
        var identity = new SkillAssetBuildIdentity("1.2.3", "abc123def456");

        SkillAssetManifest.Write(root, identity, ["mohist-explore", "mohist"]);

        Assert.True(File.Exists(Path.Combine(root, SkillAssetManifest.FileName)));
        var read = SkillAssetManifest.TryRead(root);

        Assert.True(read.IsFound);
        Assert.Null(read.Error);
        Assert.NotNull(read.Data);
        Assert.Equal(1, read.Data!.SchemaVersion);
        Assert.Equal("1.2.3", read.Data.Version);
        Assert.Equal("abc123def456", read.Data.GitHash);
        Assert.Equal(new[] { "mohist", "mohist-explore" }, read.Data.Skills);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Write_OrdersAndDeduplicatesSkillNames()
    {
        var root = Path.Combine(_tempRoot, "normalized");

        SkillAssetManifest.Write(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hash"),
            ["zeta", "alpha", "alpha", " ", "beta"]);

        var raw = File.ReadAllText(Path.Combine(root, SkillAssetManifest.FileName));
        using var document = JsonDocument.Parse(raw);

        Assert.Equal(new[] { "alpha", "beta", "zeta" },
            document.RootElement.GetProperty("skills").EnumerateArray()
                .Select(skill => skill.GetString())
                .ToArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Write_NormalizesSkillNamesAndPreservesBuildIdentity()
    {
        var root = Path.Combine(_tempRoot, "preserve");

        SkillAssetManifest.Write(
            root,
            new SkillAssetBuildIdentity("1.0.0", "deadbeef"),
            ["mohist", "mohist-explore"]);

        var read = SkillAssetManifest.TryRead(root);

        Assert.True(read.IsFound);
        Assert.Equal("1.0.0", read.Data!.Version);
        Assert.Equal("deadbeef", read.Data.GitHash);
        Assert.Equal(new[] { "mohist", "mohist-explore" }, read.Data.Skills);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Validate_AcceptsMatchingManifestWithDeclaredSkillsAndFiles()
    {
        var root = Path.Combine(_tempRoot, "valid");
        WriteSkill(root, "mohist");
        WriteSkill(root, "mohist-explore");
        SkillAssetManifest.Write(
            root,
            new SkillAssetBuildIdentity("2.0.0", "hashA"),
            ["mohist", "mohist-explore"]);

        var result = SkillAssetManifest.Validate(
            root,
            new SkillAssetBuildIdentity("2.0.0", "hashA"),
            ["mohist", "mohist-explore"]);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Validate_ReportsMissingManifest()
    {
        var root = Path.Combine(_tempRoot, "no-manifest");
        WriteSkill(root, "mohist");

        var result = SkillAssetManifest.Validate(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hashA"),
            ["mohist"]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Manifest file", StringComparison.Ordinal)
            && error.Contains("missing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("mo update", StringComparison.Ordinal));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Validate_ReportsVersionMismatchWithRepairGuidance()
    {
        var root = Path.Combine(_tempRoot, "version-mismatch");
        WriteSkill(root, "mohist");
        WriteSkill(root, "mohist-explore");
        SkillAssetManifest.Write(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hashA"),
            ["mohist", "mohist-explore"]);

        var result = SkillAssetManifest.Validate(
            root,
            new SkillAssetBuildIdentity("2.0.0", "hashA"),
            ["mohist", "mohist-explore"]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("version '1.0.0'", StringComparison.Ordinal)
            && error.Contains("'2.0.0'", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("mo update", StringComparison.Ordinal));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Validate_ReportsGitHashMismatchWithRepairGuidance()
    {
        var root = Path.Combine(_tempRoot, "git-hash-mismatch");
        WriteSkill(root, "mohist");
        WriteSkill(root, "mohist-explore");
        SkillAssetManifest.Write(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hashA"),
            ["mohist", "mohist-explore"]);

        var result = SkillAssetManifest.Validate(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hashB"),
            ["mohist", "mohist-explore"]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("git hash", StringComparison.Ordinal)
            && error.Contains("hashA", StringComparison.Ordinal)
            && error.Contains("hashB", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("mo update", StringComparison.Ordinal));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Validate_ReportsOmittedBuiltInSkillNames()
    {
        var root = Path.Combine(_tempRoot, "omitted-skill");
        WriteSkill(root, "mohist");
        SkillAssetManifest.Write(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hashA"),
            ["mohist"]);

        var result = SkillAssetManifest.Validate(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hashA"),
            ["mohist", "mohist-explore"]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("mohist-explore", StringComparison.Ordinal)
            && error.Contains("not list", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("mo update", StringComparison.Ordinal));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Validate_ReportsMissingSkillMarkdown()
    {
        var root = Path.Combine(_tempRoot, "missing-skill-file");
        WriteSkill(root, "mohist");
        SkillAssetManifest.Write(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hashA"),
            ["mohist", "mohist-explore"]);

        var result = SkillAssetManifest.Validate(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hashA"),
            ["mohist", "mohist-explore"]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("mohist-explore", StringComparison.Ordinal)
            && error.Contains("SKILL.md", StringComparison.Ordinal)
            && error.Contains("missing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("mo update", StringComparison.Ordinal));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Validate_ReportsMalformedManifest()
    {
        var root = Path.Combine(_tempRoot, "malformed");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, SkillAssetManifest.FileName), "{ not valid json");

        var result = SkillAssetManifest.Validate(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hashA"),
            ["mohist"]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("mo update", StringComparison.Ordinal));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Validate_DoesNotReadDotMohistSkills()
    {
        var root = Path.Combine(_tempRoot, "no-runtime-touch");
        WriteSkill(root, "mohist");
        WriteSkill(root, "mohist-explore");
        SkillAssetManifest.Write(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hashA"),
            ["mohist", "mohist-explore"]);

        var mohistSkillsDir = Path.Combine(root, ".mohist", "skills");
        Directory.CreateDirectory(mohistSkillsDir);
        var sentinelPath = Path.Combine(mohistSkillsDir, "sentinel.txt");
        File.WriteAllText(sentinelPath, "keep");

        var result = SkillAssetManifest.Validate(
            root,
            new SkillAssetBuildIdentity("1.0.0", "hashA"),
            ["mohist", "mohist-explore"]);

        Assert.True(result.IsValid);
        Assert.True(File.Exists(sentinelPath));
        Assert.Equal("keep", File.ReadAllText(sentinelPath));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void ResolveBuildIdentity_PrefersInformationalVersionSplit()
    {
        var identity = SkillAssetManifest.ResolveBuildIdentity(
            "9.9.9+abcdef0123",
            "9.9.9.0",
            () => "envhash",
            () => "headhash");

        Assert.Equal("9.9.9", identity.Version);
        Assert.Equal("abcdef0123", identity.GitHash);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void ResolveBuildIdentity_FallsBackToEnvThenGitHead()
    {
        var fromEnv = SkillAssetManifest.ResolveBuildIdentity(
            "1.0.0",
            "1.0.0.0",
            () => "envhash",
            () => "headhash");
        Assert.Equal("envhash", fromEnv.GitHash);

        var fromHead = SkillAssetManifest.ResolveBuildIdentity(
            "1.0.0",
            "1.0.0.0",
            () => null,
            () => "headhash");
        Assert.Equal("headhash", fromHead.GitHash);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void ResolveCurrentBuildIdentity_ReturnsNonEmptyVersionForMohistCli()
    {
        var identity = SkillAssetManifest.ResolveCurrentBuildIdentity();

        Assert.NotNull(identity.Version);
        Assert.NotEmpty(identity.Version);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void ResolveBuildIdentity_ToleratesNullInputs()
    {
        var identity = SkillAssetManifest.ResolveBuildIdentity(
            informationalVersion: null,
            versionFromAssembly: "3.0.0.0",
            getEnvHash: () => null,
            getGitHeadFromRepo: () => null);

        Assert.Equal("3.0.0.0", identity.Version);
        Assert.Null(identity.GitHash);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void ResolveBuildIdentity_FallsBackToAssemblyVersionWhenInformationalMissing()
    {
        var identity = SkillAssetManifest.ResolveBuildIdentity(
            informationalVersion: null,
            versionFromAssembly: "4.5.6.7",
            getEnvHash: () => null,
            getGitHeadFromRepo: () => null);

        Assert.Equal("4.5.6.7", identity.Version);
    }

    private static void WriteSkill(string root, string name)
    {
        Directory.CreateDirectory(Path.Combine(root, name));
        File.WriteAllText(
            Path.Combine(root, name, "SKILL.md"),
            $"---\nname: {name}\ndescription: test\n---\n\n# {name}\n");
    }
}
