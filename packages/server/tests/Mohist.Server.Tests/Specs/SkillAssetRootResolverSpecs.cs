using Mohist.Cli;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("SkillsCli")]
public sealed class SkillAssetRootResolverSpecs : IDisposable
{
    private static readonly SkillAssetBuildIdentity CurrentIdentity =
        SkillAssetManifest.ResolveCurrentBuildIdentity();

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mohist-skill-resolver-{Guid.NewGuid():N}");
    private readonly string? _originalOverrideEnv;

    public SkillAssetRootResolverSpecs()
    {
        Directory.CreateDirectory(_tempRoot);
        _originalOverrideEnv = Environment.GetEnvironmentVariable(SkillAssetRootResolver.OverrideEnvironmentVariable);
        Environment.SetEnvironmentVariable(SkillAssetRootResolver.OverrideEnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            SkillAssetRootResolver.OverrideEnvironmentVariable,
            _originalOverrideEnv);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

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

        Assert.False(resolution.IsSelected);
        Assert.Null(resolution.AssetRoot);
        Assert.Equal(SkillAssetRootSource.Override, resolution.Source);
        Assert.Equal(Path.GetFullPath(missingOverride), Path.GetFullPath(resolution.AttemptedRoot!));
        Assert.Contains("MOHIST_SKILLS_DIR", resolution.DiagnosticSummary, StringComparison.Ordinal);
        Assert.Contains("mo update", resolution.DiagnosticSummary, StringComparison.Ordinal);
        Assert.Contains("scripts/install-mo.sh", resolution.DiagnosticSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_FallsBackToSiblingPublishDirectory_WhenManagedCacheIsAbsent()
    {
        var home = Path.Combine(_tempRoot, "user-home-without-cache");
        var siblingRoot = Path.Combine(AppContext.BaseDirectory, "skill-data");
        Directory.CreateDirectory(siblingRoot);

        var resolver = CreateResolver(home, identity: CurrentIdentity);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

        Assert.Equal(SkillAssetRootSource.SiblingFallback, resolution.Source);
        Assert.Equal(Path.GetFullPath(siblingRoot), Path.GetFullPath(resolution.AssetRoot!));
    }

    [Fact]
    public void Resolve_FallsBackToSibling_OnlyWhenNoValidManagedCacheExists()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteCompatibleAssetRoot(managedRoot);
        var siblingRoot = Path.Combine(AppContext.BaseDirectory, "skill-data");
        Directory.CreateDirectory(siblingRoot);

        var resolver = CreateResolver(home, identity: CurrentIdentity);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.Equal(Path.GetFullPath(managedRoot), Path.GetFullPath(resolution.AssetRoot!));
    }

    [Fact]
    public void Resolve_ReportsManagedManifestMissing_WithRepairGuidance()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteSkill(managedRoot, "mohist");
        WriteSkill(managedRoot, "mohist-explore");

        var resolver = CreateResolver(home, identity: CurrentIdentity);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

        Assert.False(resolution.IsSelected);
        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.Equal(Path.GetFullPath(managedRoot), Path.GetFullPath(resolution.AttemptedRoot!));
        Assert.Contains("manifest", resolution.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing", resolution.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo update", resolution.DiagnosticSummary, StringComparison.Ordinal);
        Assert.Contains("scripts/install-mo.sh", resolution.DiagnosticSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReportsManagedVersionMismatch_WithoutFallingBackToSibling()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteSkill(managedRoot, "mohist");
        WriteSkill(managedRoot, "mohist-explore");
        SkillAssetManifest.Write(
            managedRoot,
            new SkillAssetBuildIdentity("0.0.0-stale", "deadbeef"),
            new[] { "mohist", "mohist-explore" });

        var resolver = CreateResolver(home, identity: CurrentIdentity);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

        Assert.False(resolution.IsSelected);
        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.Equal(Path.GetFullPath(managedRoot), Path.GetFullPath(resolution.AttemptedRoot!));
        Assert.Contains("version '0.0.0-stale'", resolution.DiagnosticSummary, StringComparison.Ordinal);
        Assert.Contains("mo update", resolution.DiagnosticSummary, StringComparison.Ordinal);
        Assert.Contains("scripts/install-mo.sh", resolution.DiagnosticSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReportsManagedGitHashMismatch_WithRepairGuidance()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteSkill(managedRoot, "mohist");
        WriteSkill(managedRoot, "mohist-explore");
        var compatibleVersion = CurrentIdentity.Version ?? "1.0.0";
        SkillAssetManifest.Write(
            managedRoot,
            new SkillAssetBuildIdentity(compatibleVersion, "stale-hash"),
            new[] { "mohist", "mohist-explore" });

        var resolver = CreateResolver(home, identity: CurrentIdentity);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

        Assert.False(resolution.IsSelected);
        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.Contains("git hash", resolution.DiagnosticSummary, StringComparison.Ordinal);
        Assert.Contains("stale-hash", resolution.DiagnosticSummary, StringComparison.Ordinal);
        Assert.Contains("mo update", resolution.DiagnosticSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReportsOmittedBuiltInSkill_WithRepairGuidance()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteSkill(managedRoot, "mohist");
        WriteSkill(managedRoot, "mohist-explore");
        SkillAssetManifest.Write(
            managedRoot,
            CurrentIdentity,
            new[] { "mohist" });

        var resolver = CreateResolver(home, identity: CurrentIdentity);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

        Assert.False(resolution.IsSelected);
        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.Contains("mohist-explore", resolution.DiagnosticSummary, StringComparison.Ordinal);
        Assert.Contains("not list", resolution.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo update", resolution.DiagnosticSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReportsMissingSkillMarkdownFile_WithRepairGuidance()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteSkill(managedRoot, "mohist");
        SkillAssetManifest.Write(
            managedRoot,
            CurrentIdentity,
            new[] { "mohist", "mohist-explore" });

        var resolver = CreateResolver(home, identity: CurrentIdentity);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

        Assert.False(resolution.IsSelected);
        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.Contains("mohist-explore", resolution.DiagnosticSummary, StringComparison.Ordinal);
        Assert.Contains("SKILL.md", resolution.DiagnosticSummary, StringComparison.Ordinal);
        Assert.Contains("missing", resolution.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo update", resolution.DiagnosticSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReportsMalformedManagedManifestJson_WithRepairGuidance()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteSkill(managedRoot, "mohist");
        Directory.CreateDirectory(managedRoot);
        File.WriteAllText(Path.Combine(managedRoot, SkillAssetManifest.FileName), "{ not valid json");

        var resolver = CreateResolver(home, identity: CurrentIdentity);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

        Assert.False(resolution.IsSelected);
        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.Contains("not valid JSON", resolution.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo update", resolution.DiagnosticSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReportsNoRootDiagnostic_WhenNothingIsAvailable()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var siblingRoot = Path.Combine(AppContext.BaseDirectory, "skill-data");
        var siblingRootExisted = Directory.Exists(siblingRoot);
        var siblingBackup = siblingRootExisted
            ? Path.Combine(_tempRoot, "sibling-backup")
            : null;

        try
        {
            if (siblingRootExisted)
                Directory.Move(siblingRoot, siblingBackup!);

            var resolver = CreateResolver(home, identity: CurrentIdentity);

            var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

            Assert.False(resolution.IsSelected);
            Assert.Equal(SkillAssetRootSource.None, resolution.Source);
            Assert.Contains("MOHIST_SKILLS_DIR", resolution.DiagnosticSummary, StringComparison.Ordinal);
            Assert.Contains(".mohist/cli/skill-data", resolution.DiagnosticSummary, StringComparison.Ordinal);
            Assert.Contains("mo update", resolution.DiagnosticSummary, StringComparison.Ordinal);
            Assert.Contains("scripts/install-mo.sh", resolution.DiagnosticSummary, StringComparison.Ordinal);
        }
        finally
        {
            if (siblingRootExisted && siblingBackup is not null && Directory.Exists(siblingBackup))
            {
                if (Directory.Exists(siblingRoot))
                    Directory.Delete(siblingRoot, recursive: true);
                Directory.Move(siblingBackup, siblingRoot);
            }
        }
    }

    [Fact]
    public void Resolve_PropagatesManifestCompatibilityDiagnostic_ToSkillAssetService()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteSkill(managedRoot, "mohist");
        WriteSkill(managedRoot, "mohist-explore");
        SkillAssetManifest.Write(
            managedRoot,
            new SkillAssetBuildIdentity("0.0.0-stale", "deadbeef"),
            new[] { "mohist", "mohist-explore" });

        var resolver = CreateResolver(home, identity: CurrentIdentity);
        var service = new SkillAssetService(resolver);

        var result = service.GetSkill("mohist", includeSupplementaryFiles: false);

        Assert.False(result.Found);
        Assert.NotNull(result.Error);
        Assert.Contains("mo update", result.Error!, StringComparison.Ordinal);
        Assert.Contains("scripts/install-mo.sh", result.Error!, StringComparison.Ordinal);
        Assert.Contains("0.0.0-stale", result.Error!, StringComparison.Ordinal);
        Assert.NotNull(service.ResolverDiagnostic);
        Assert.Contains("mo update", service.ResolverDiagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DoesNotReadWriteOrMutateRuntimeDotMohistSkills()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var managedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        WriteCompatibleAssetRoot(managedRoot);

        var runtimeSkillsDir = Path.Combine(home, ".mohist", "skills");
        Directory.CreateDirectory(runtimeSkillsDir);
        var sentinelPath = Path.Combine(runtimeSkillsDir, "sentinel.txt");
        var nestedDir = Path.Combine(runtimeSkillsDir, "mohist");
        var nestedSentinel = Path.Combine(nestedDir, "SKILL.md");
        File.WriteAllText(sentinelPath, "keep-runtime");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(nestedSentinel, "keep-nested");

        var before = SnapshotDirectory(runtimeSkillsDir);

        var resolver = CreateResolver(home, identity: CurrentIdentity);
        var service = new SkillAssetService(resolver);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);
        var list = service.ListVisibleSkills();
        var getResult = service.GetSkill("mohist", includeSupplementaryFiles: true);

        Assert.Equal(SkillAssetRootSource.ManagedCache, resolution.Source);
        Assert.NotEmpty(list);
        Assert.True(getResult.Found, getResult.Error);

        var after = SnapshotDirectory(runtimeSkillsDir);
        Assert.Equal(before, after);

        Assert.True(File.Exists(sentinelPath));
        Assert.Equal("keep-runtime", File.ReadAllText(sentinelPath));
        Assert.True(File.Exists(nestedSentinel));
        Assert.Equal("keep-nested", File.ReadAllText(nestedSentinel));
    }

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
            new[] { "mohist", "mohist-explore" });

        var runtimeSkillsDir = Path.Combine(home, ".mohist", "skills");
        Directory.CreateDirectory(runtimeSkillsDir);
        var sentinelPath = Path.Combine(runtimeSkillsDir, "sentinel.txt");
        File.WriteAllText(sentinelPath, "keep-runtime");

        var resolver = CreateResolver(home, identity: CurrentIdentity);
        var service = new SkillAssetService(resolver);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);
        var getResult = service.GetSkill("mohist", includeSupplementaryFiles: true);

        Assert.False(resolution.IsSelected);
        Assert.False(getResult.Found);
        Assert.True(File.Exists(sentinelPath));
        Assert.Equal("keep-runtime", File.ReadAllText(sentinelPath));
    }

    [Fact]
    public void Resolve_DefaultManagedCacheLocation_IsUserHomeDotMohistCliSkillData()
    {
        var home = Path.Combine(_tempRoot, "user-home");
        var expectedManagedRoot = Path.Combine(home, ".mohist", "cli", "skill-data");
        Directory.CreateDirectory(expectedManagedRoot);

        var resolver = CreateResolver(home, identity: CurrentIdentity);

        var resolution = resolver.Resolve(SkillAssetService.BuiltInSkillNames);

        Assert.Equal(
            Path.GetFullPath(expectedManagedRoot),
            Path.GetFullPath(resolution.AttemptedRoot ?? string.Empty));
    }

    [Fact]
    public void DefaultManagedAssetRoot_UsesUserProfileWhenAvailable_AndSiblingDirectoryOtherwise()
    {
        var computed = SkillAssetRootResolver.DefaultManagedAssetRoot();

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = string.IsNullOrWhiteSpace(profile)
            ? Path.Combine(AppContext.BaseDirectory, "skill-data")
            : Path.Combine(profile, ".mohist", "cli", "skill-data");

        Assert.Equal(
            Path.GetFullPath(expected),
            Path.GetFullPath(computed));
    }

    private static SkillAssetRootResolver CreateResolver(
        string home,
        SkillAssetBuildIdentity identity,
        Func<string?>? getOverrideAssetRoot = null)
    {
        return new SkillAssetRootResolver(
            getOverrideAssetRoot: getOverrideAssetRoot,
            getManagedAssetRoot: null,
            getUserHome: () => home,
            getBuildIdentity: () => identity);
    }

    private static void WriteCompatibleAssetRoot(string root)
    {
        Directory.CreateDirectory(root);
        WriteSkill(root, "mohist");
        WriteSkill(root, "mohist-explore");
        SkillAssetManifest.Write(
            root,
            CurrentIdentity,
            new[] { "mohist", "mohist-explore" });
    }

    private static void WriteSkill(string root, string name)
    {
        Directory.CreateDirectory(Path.Combine(root, name));
        File.WriteAllText(
            Path.Combine(root, name, "SKILL.md"),
            $"---\nname: {name}\ndescription: {DescriptionFor(name)}\n---\n\n# {name}\n");
    }

    private static IReadOnlyList<string> SnapshotDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<string>();

        return Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToArray();
    }

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "从产品和用户视角探索 mohist 项目，发现功能缺陷、体验问题、设计机会和价值增长点。当用户想要探索代码库、发现改进点、审查用户体验、思考功能设计、或无目标地巡检产品时使用。触发词包括 \"explore\"、\"探索\"、\"巡检\"、\"找问题\"、\"体验审查\"、\"功能设计\"、\"产品思考\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
