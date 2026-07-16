using Mohist.Server.UnitTests.Support;
using Mohist.Cli;
using EnvironmentAbstractions.TestHelpers;
using Xunit;

namespace Mohist.Server.UnitTests.Skills;

public sealed class SkillAssetRootResolverTests
{
    private readonly FakeFileSystem _files = new();
    private readonly MockEnvironmentVariableProvider _environment = new();
    private readonly string _tempRoot = Path.Combine("/tmp", $"mohist-skill-resolver-{Guid.NewGuid():N}");

    public SkillAssetRootResolverTests()
    {
        _environment[SkillAssetRootResolver.OverrideEnvironmentVariable] = null;
    }

    [Fact]
    public void Resolve_PrefersManagedCacheUnderUserHome_AsDefaultInstalledRoot()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteAssetRoot(managedRoot);

        var resolver = CreateResolver(home);

        var resolution = resolver.Resolve();

        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.Equal(Path.GetFullPath(managedRoot, "/"), Path.GetFullPath(resolution.AssetRoot!, "/"));
        Assert.True(resolution.IsSelected);
        Assert.Null(resolution.DiagnosticSummary);
    }

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
        Assert.Equal(Path.GetFullPath(overrideRoot, "/"), Path.GetFullPath(resolution.AssetRoot!, "/"));
    }

    [Fact]
    public void Resolve_PrefersOverrideDirectory_OverSiblingFallback_WhenManagedCacheIsAbsent()
    {
        var home = Path.Combine(_tempRoot, "user-home-without-cache");
        var overrideRoot = Path.Combine(_tempRoot, "override-assets");
        WriteAssetRoot(overrideRoot);

        var resolver = CreateResolver(home, getOverrideAssetRoot: () => overrideRoot);

        var resolution = resolver.Resolve();

        Assert.Equal(SkillAssetRootSource.Override, resolution.Source);
        Assert.Equal(Path.GetFullPath(overrideRoot, "/"), Path.GetFullPath(resolution.AssetRoot!, "/"));
    }

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

    [Fact]
    public void Resolve_FallsBackToSiblingRoot_WhenManagedCacheIsAbsent()
    {
        const string siblingRoot = "/mohist-tests/app/skill-data";
        _files.AddDirectory(siblingRoot);
        WriteSkill(siblingRoot, "mohist");

        var home = Path.Combine(_tempRoot, "user-home");
        var resolver = CreateResolver(home);

        var resolution = resolver.Resolve();

        Assert.Equal(SkillAssetRootSource.SiblingFallback, resolution.Source);
        Assert.Equal(
            Path.GetFullPath(siblingRoot, "/"),
            Path.GetFullPath(resolution.AssetRoot!, "/"));
    }

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
            Path.GetFullPath(expectedManagedRoot, "/"),
            Path.GetFullPath(resolution.AssetRoot!, "/"));
    }

    [Fact]
    public void Resolve_ReportsManagedPath_InDiagnostic_WhenNoAssetRootFound()
    {
        const string home = "/mohist-tests/user-home";
        var resolver = CreateResolver(home);

        var computed = resolver.DefaultManagedAssetRoot();

        Assert.Equal(
            Path.Combine(home, ".mohist", "cli", "skill-data"),
            computed);
    }

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
            getUserHome: () => home,
            getSiblingAssetRoot: () => "/mohist-tests/app/skill-data");
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

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue 或 epic，查看项目状态或日志，或任何涉及 Mohist issue/epic/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。当用户带着一句话、一个模糊念头或未沉淀的改进意图，需要探索当前产品形态和技术实现，最终产出一份用户视角、产品视角、领域视角三段协作的 PRD 时使用。触发词包括 \"提炼需求\"、\"写 PRD\"、\"沉淀 issue\"、\"需求文档\"、\"探索\"、\"完善 issue\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
