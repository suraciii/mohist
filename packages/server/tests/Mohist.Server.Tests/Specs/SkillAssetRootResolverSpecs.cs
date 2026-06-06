using Mohist.Cli;
using Mohist.Server.Tests.Support;
using EnvironmentAbstractions.TestHelpers;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("SkillsCli")]
public sealed class SkillAssetRootResolverSpecs
{
    private static readonly SkillAssetBuildIdentity CurrentIdentity =
        SkillAssetManifest.ResolveCurrentBuildIdentity();

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
        WriteCompatibleAssetRoot(managedRoot);

        var resolver = CreateResolver(home, identity: CurrentIdentity);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

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
        WriteCompatibleAssetRoot(managedRoot);
        var overrideRoot = Path.Combine(_tempRoot, "override-assets");
        WriteCompatibleAssetRoot(overrideRoot);

        var resolver = CreateResolver(
            home,
            identity: CurrentIdentity,
            getOverrideAssetRoot: () => overrideRoot);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

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
        WriteCompatibleAssetRoot(overrideRoot);

        var resolver = CreateResolver(
            home,
            identity: CurrentIdentity,
            getOverrideAssetRoot: () => overrideRoot);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

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
        WriteCompatibleAssetRoot(managedRoot);
        var missingOverride = Path.Combine(_tempRoot, "missing-override");

        var resolver = CreateResolver(
            home,
            identity: CurrentIdentity,
            getOverrideAssetRoot: () => missingOverride);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

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
        var sourceRoot = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "cli", "Mohist.Cli", "skill-data");
        sourceRoot = Path.GetFullPath(sourceRoot);
        if (Directory.Exists(sourceRoot))
        {
            _files.AddDirectory(siblingRoot);
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                _files.AddFile(Path.Combine(siblingRoot, relative), File.ReadAllText(file));
            }
            SkillAssetManifest.Write(
                siblingRoot,
                CurrentIdentity,
                new[] { "mohist", "mohist-explore" },
                _files);
        }

        var home = Path.Combine(_tempRoot, "user-home");
        var resolver = CreateResolver(home, identity: CurrentIdentity);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

        Assert.Equal(SkillAssetRootSource.SiblingFallback, resolution.Source);
        Assert.Equal(
            Path.GetFullPath(siblingRoot),
            Path.GetFullPath(resolution.AssetRoot!));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Resolve_TreatsIncompatibleManagedCache_AsFailed_AndReportsRepairGuidance()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteSkill(managedRoot, "mohist");
        WriteSkill(managedRoot, "mohist-explore");
        SkillAssetManifest.Write(
            managedRoot,
            new SkillAssetBuildIdentity("0.0.0-stale", "deadbeef"),
            new[] { "mohist", "mohist-explore" },
            _files);

        var resolver = CreateResolver(home, identity: CurrentIdentity);
        var service = new SkillAssetService(_files, resolver);

        var result = service.GetSkill("mohist", includeSupplementaryFiles: false);

        Assert.False(result.Found);
        Assert.NotNull(result.Error);
        Assert.Contains("mo update", result.Error!, StringComparison.Ordinal);
        Assert.Contains("scripts/install-mo.sh", result.Error!, StringComparison.Ordinal);
        Assert.Contains("0.0.0-stale", result.Error!, StringComparison.Ordinal);
        Assert.NotNull(service.ResolverDiagnostic);
        Assert.Contains("mo update", service.ResolverDiagnostic!, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Resolve_DoesNotReadWriteOrMutateRuntimeDotMohistSkills()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteCompatibleAssetRoot(managedRoot);

        var runtimeSkillsDir = Path.Combine(home, ".mohist", "skills");
        _files.AddDirectory(runtimeSkillsDir);
        var sentinelPath = Path.Combine(runtimeSkillsDir, "sentinel.txt");
        _files.AddFile(sentinelPath, "keep-runtime");
        var nestedDir = Path.Combine(runtimeSkillsDir, "mohist");
        _files.AddDirectory(nestedDir);
        var nestedSentinel = Path.Combine(nestedDir, "SKILL.md");
        _files.AddFile(nestedSentinel, "keep-nested");

        var resolver = CreateResolver(home, identity: CurrentIdentity);
        var service = new SkillAssetService(_files, resolver);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);
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
    public void Resolve_DoesNotReadWriteOrMutateRuntimeDotMohistSkills_WhenResolutionFails()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteSkill(managedRoot, "mohist");
        WriteSkill(managedRoot, "mohist-explore");
        SkillAssetManifest.Write(
            managedRoot,
            new SkillAssetBuildIdentity("0.0.0-stale", "deadbeef"),
            new[] { "mohist", "mohist-explore" },
            _files);

        var runtimeSkillsDir = Path.Combine(home, ".mohist", "skills");
        _files.AddDirectory(runtimeSkillsDir);
        var sentinelPath = Path.Combine(runtimeSkillsDir, "sentinel.txt");
        _files.AddFile(sentinelPath, "keep-runtime");

        var resolver = CreateResolver(home, identity: CurrentIdentity);
        var service = new SkillAssetService(_files, resolver);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);
        var getResult = service.GetSkill("mohist", includeSupplementaryFiles: true);

        Assert.False(resolution.IsSelected);
        Assert.False(getResult.Found);
        Assert.True(_files.HasFile(sentinelPath));
        Assert.Equal("keep-runtime", _files.ReadAllText(sentinelPath));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Resolve_DefaultManagedCacheLocation_IsUserHomeDotMohistCliSkillData()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var expectedManagedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteCompatibleAssetRoot(expectedManagedRoot);

        var resolver = CreateResolver(home, identity: CurrentIdentity);
        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

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

    private SkillAssetRootResolver CreateResolver(
        string home,
        SkillAssetBuildIdentity identity,
        Func<string?>? getOverrideAssetRoot = null)
    {
        return new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: getOverrideAssetRoot,
            getManagedAssetRoot: null,
            getUserHome: () => home,
            getBuildIdentity: () => identity);
    }

    private void WriteCompatibleAssetRoot(string root)
    {
        _files.AddDirectory(root);
        WriteSkill(root, "mohist");
        WriteSkill(root, "mohist-explore");
        SkillAssetManifest.Write(
            root,
            CurrentIdentity,
            new[] { "mohist", "mohist-explore" },
            _files);
    }

    private void WriteSkill(string root, string name)
    {
        var skillDir = Path.Combine(root, name);
        _files.AddDirectory(skillDir);
        _files.AddFile(
            Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: {name}\ndescription: {DescriptionFor(name)}\n---\n\n# {name}\n");
    }

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "从产品和用户视角探索 mohist 项目，发现功能缺陷、体验问题、设计机会和价值增长点。当用户想要探索代码库、发现改进点、审查用户体验、思考功能设计、或无目标地巡检产品时使用。触发词包括 \"explore\"、\"探索\"、\"巡检\"、\"找问题\"、\"体验审查\"、\"功能设计\"、\"产品思考\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
