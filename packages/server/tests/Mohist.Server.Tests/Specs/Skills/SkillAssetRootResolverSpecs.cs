using Mohist.Cli;
using Mohist.Server.Tests.Support;
using EnvironmentAbstractions.TestHelpers;
using Xunit;

namespace Mohist.Server.Tests.Specs.Skills;

[Collection("SkillsCli")]
public sealed class SkillAssetRootResolverSpecs
{
    private readonly FakeFileSystem _files = new();
    private readonly MockEnvironmentVariableProvider _environment = new();
    private readonly string _tempRoot = Path.Combine("/tmp", $"mohist-skill-resolver-{Guid.NewGuid():N}");

    public SkillAssetRootResolverSpecs()
    {
        _environment[SkillAssetRootResolver.OverrideEnvironmentVariable] = null;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Resolve_PrefersManagedCacheUnderUserHome_AsDefaultInstalledRoot()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteAssetRoot(managedRoot);

        var resolver = CreateResolver(home);

        var resolution = resolver.Resolve();

        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.Equal(Path.GetFullPath(managedRoot), Path.GetFullPath(resolution.AssetRoot!));
        Assert.True(resolution.IsSelected);
        Assert.Null(resolution.DiagnosticSummary);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Resolve_PrefersOverrideDirectory_OverManagedCacheAndSiblingFallback()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteAssetRoot(managedRoot);
        var overrideRoot = Path.Combine(_tempRoot, "override-assets");
        WriteAssetRoot(overrideRoot);

        var resolver = CreateResolver(home, getOverrideAssetRoot: () => overrideRoot);

        var resolution = resolver.Resolve();

        Assert.Equal(SkillAssetRootSource.Override, resolution.Source);
        Assert.Equal(Path.GetFullPath(overrideRoot), Path.GetFullPath(resolution.AssetRoot!));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Resolve_PrefersOverrideDirectory_OverSiblingFallback_WhenManagedCacheIsAbsent()
    {
        var home = Path.Combine(_tempRoot, "user-home-without-cache");
        var overrideRoot = Path.Combine(_tempRoot, "override-assets");
        WriteAssetRoot(overrideRoot);

        var resolver = CreateResolver(home, getOverrideAssetRoot: () => overrideRoot);

        var resolution = resolver.Resolve();

        Assert.Equal(SkillAssetRootSource.Override, resolution.Source);
        Assert.Equal(Path.GetFullPath(overrideRoot), Path.GetFullPath(resolution.AssetRoot!));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Resolve_TreatsOverrideThatPointsToMissingDirectory_AsUnrecoverable()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteAssetRoot(managedRoot);
        var missingOverride = Path.Combine(_tempRoot, "missing-override");

        var resolver = CreateResolver(home, getOverrideAssetRoot: () => missingOverride);

        var resolution = resolver.Resolve();

        Assert.Equal(SkillAssetRootSource.Override, resolution.Source);
        Assert.False(resolution.IsSelected);
        Assert.Contains("does not exist", resolution.DiagnosticSummary!, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Resolve_FallsBackToSiblingRoot_WhenManagedCacheIsAbsent()
    {
        var siblingRoot = Path.Combine(AppContext.BaseDirectory, "skill-data");
        _files.AddDirectory(siblingRoot);
        WriteSkill(siblingRoot, "mohist");

        var home = Path.Combine(_tempRoot, "user-home");
        var resolver = CreateResolver(home);

        var resolution = resolver.Resolve();

        Assert.Equal(SkillAssetRootSource.SiblingFallback, resolution.Source);
        Assert.Equal(
            Path.GetFullPath(siblingRoot),
            Path.GetFullPath(resolution.AssetRoot!));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Resolve_DoesNotReadWriteOrMutateRuntimeDotMohistSkills()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteAssetRoot(managedRoot);

        var runtimeSkillsDir = Path.Combine(home, ".mohist", "skills");
        _files.AddDirectory(runtimeSkillsDir);
        var sentinelPath = Path.Combine(runtimeSkillsDir, "sentinel.txt");
        _files.AddFile(sentinelPath, "keep-runtime");
        var nestedDir = Path.Combine(runtimeSkillsDir, "mohist");
        _files.AddDirectory(nestedDir);
        var nestedSentinel = Path.Combine(nestedDir, "SKILL.md");
        _files.AddFile(nestedSentinel, "keep-nested");

        var resolver = CreateResolver(home);
        var service = new SkillAssetService(_files, resolver);

        var resolution = resolver.Resolve();
        var list = service.ListVisibleSkills();
        var getResult = service.GetSkill("mohist", includeSupplementaryFiles: true);

        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.NotEmpty(list);
        Assert.True(getResult.Found, getResult.Error);

        Assert.True(_files.HasFile(sentinelPath));
        Assert.Equal("keep-runtime", _files.ReadAllText(sentinelPath));
        Assert.True(_files.HasFile(nestedSentinel));
        Assert.Equal("keep-nested", _files.ReadAllText(nestedSentinel));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Resolve_DefaultManagedCacheLocation_IsUserHomeDotMohistCliSkillData()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var expectedManagedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteAssetRoot(expectedManagedRoot);

        var resolver = CreateResolver(home);
        var resolution = resolver.Resolve();

        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.Equal(
            Path.GetFullPath(expectedManagedRoot),
            Path.GetFullPath(resolution.AssetRoot!));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Resolve_ReportsSiblingPath_InDiagnostic_WhenNoAssetRootFound()
    {
        var resolver = new SkillAssetRootResolver(_files, _environment);

        var computed = resolver.DefaultManagedAssetRoot();

        var home = _environment.GetEnvironmentVariable(SkillAssetRootResolver.HomeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = string.IsNullOrWhiteSpace(home)
            ? Path.Combine(AppContext.BaseDirectory, "skill-data")
            : Path.Combine(home, ".mohist", "cli", "skill-data");

        Assert.Equal(
            Path.GetFullPath(expected),
            Path.GetFullPath(computed));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void GetSkill_ReturnsUnknown_WhenManagedCacheHasNoMatchingSkill()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        _files.AddDirectory(managedRoot);

        var resolver = CreateResolver(home);
        var service = new SkillAssetService(_files, resolver);

        var result = service.GetSkill("mohist", includeSupplementaryFiles: false);

        Assert.False(result.Found);
        Assert.Contains("Unknown Mohist built-in skill", result.Error!, StringComparison.Ordinal);
    }

    private SkillAssetRootResolver CreateResolver(
        string home,
        Func<string?>? getOverrideAssetRoot = null)
    {
        return new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: getOverrideAssetRoot,
            getManagedAssetRoot: null,
            getUserHome: () => home);
    }

    private void WriteAssetRoot(string root)
    {
        _files.AddDirectory(root);
        WriteSkill(root, "mohist");
        WriteSkill(root, "mohist-explore");
    }

    private void WriteSkill(string root, string name)
    {
        var skillDir = Path.Combine(root, name);
        _files.AddDirectory(skillDir);
        _files.AddFile(
            Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: {name}\ndescription: Test skill {name}.\n---\n\n# {name}\n");
    }
}
