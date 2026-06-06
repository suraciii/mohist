using global::System.Text.Json;
using global::System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using EnvironmentAbstractions.TestHelpers;
using Xunit;

namespace Mohist.Server.Tests.Specs.Skills;

[Collection("SkillsCli")]
public sealed class SkillsContentSpecs
{
    private readonly FakeFileSystem _files = new();
    private readonly MockEnvironmentVariableProvider _environment = new();
    private readonly string _defaultAssetRoot;

    public SkillsContentSpecs()
    {
        _defaultAssetRoot = PopulateDefaultAssets();
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task List_PrintsVisibleBuiltInSkillsSortedByName()
    {
        using var stdout = new StringWriter();

        var exitCode = await BuildRootCommand(stdout).Parse(["skills", "list"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var lines = SplitLines(stdout.ToString());
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("mohist\t", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("mohist-explore\t", lines[1], StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task List_Json_ReturnsStructuredSortedEntries()
    {
        using var stdout = new StringWriter();

        var exitCode = await BuildRootCommand(stdout).Parse(["skills", "list", "--json"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var items = JsonSerializer.Deserialize<List<SkillListItem>>(stdout.ToString());
        Assert.NotNull(items);
        Assert.Collection(
            items!,
            item =>
            {
                Assert.Equal("mohist", item.Name);
                Assert.False(string.IsNullOrWhiteSpace(item.Description));
            },
            item =>
            {
                Assert.Equal("mohist-explore", item.Name);
                Assert.False(string.IsNullOrWhiteSpace(item.Description));
            });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task Get_PrintsPackagedFullGuidance_NotInstalledStub()
    {
        var agentsStubDir = Path.Combine("/tmp", $"agents-stubs-{Guid.NewGuid():N}", ".agents", "skills", "mohist");
        _files.AddDirectory(agentsStubDir);
        _files.AddFile(Path.Combine(agentsStubDir, "SKILL.md"), "stub");

        using var stdout = new StringWriter();

        var exitCode = await BuildRootCommand(stdout).Parse(["skills", "get", "mohist"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var content = stdout.ToString();
        Assert.Contains("name: mohist", content);
        Assert.DoesNotContain("stub", content);
        Assert.DoesNotContain("Run `mo skills get mohist`", content);
        Assert.DoesNotContain("mo skills get mohist --full", content, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task Get_Json_ReturnsNameDescriptionAndContent()
    {
        using var stdout = new StringWriter();

        var exitCode = await BuildRootCommand(stdout).Parse(["skills", "get", "mohist", "--json"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var item = JsonNode.Parse(stdout.ToString())!.AsObject();
        Assert.Equal("mohist", item["name"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(item["description"]!.GetValue<string>()));
        Assert.Contains("name: mohist", item["content"]!.GetValue<string>());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task Get_Full_AppendsSupplementaryFilesInDeterministicOrder()
    {
        using var stdout = new StringWriter();

        var exitCode = await BuildRootCommand(stdout).Parse(["skills", "get", "mohist", "--full"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var content = stdout.ToString();
        var marker = "--- references/issue-templates.md ---";
        Assert.Contains(marker, content);
        Assert.True(content.IndexOf(marker, StringComparison.Ordinal) > content.IndexOf("name: mohist", StringComparison.Ordinal));
        Assert.DoesNotContain("mo skills get mohist --full", content, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task Get_All_EmitsAllVisibleBuiltInSkillsInNameOrder()
    {
        using var stdout = new StringWriter();

        var exitCode = await BuildRootCommand(stdout).Parse(["skills", "get", "--all"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var content = stdout.ToString();
        var mohistIndex = content.IndexOf("## mohist", StringComparison.Ordinal);
        var exploreIndex = content.IndexOf("## mohist-explore", StringComparison.Ordinal);
        Assert.True(mohistIndex >= 0);
        Assert.True(exploreIndex > mohistIndex);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task Path_PrintsPackagedDirectoryPath_AndJsonIncludesNameAndPath()
    {
        using var stdout = new StringWriter();
        using var jsonStdout = new StringWriter();

        var exitCode = await BuildRootCommand(stdout).Parse(["skills", "path", "mohist"]).InvokeAsync();
        var jsonExitCode = await BuildRootCommand(jsonStdout).Parse(["skills", "path", "mohist", "--json"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(0, jsonExitCode);
        var textPath = stdout.ToString().Trim();
        Assert.EndsWith(Path.Combine("skill-data", "mohist"), textPath, StringComparison.Ordinal);
        var item = JsonNode.Parse(jsonStdout.ToString())!.AsObject();
        Assert.Equal("mohist", item["name"]!.GetValue<string>());
        Assert.Equal(textPath, item["path"]!.GetValue<string>());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task Path_PrintsManagedCachePath_WhenManagedCacheIsSelected()
    {
        var userHome = Path.Combine("/tmp", $"mohist-content-home-{Guid.NewGuid():N}");
        var managedRoot = Path.Combine(userHome, ".mohist", "cli", "skill-data");
        _files.AddDirectory(managedRoot);
        WriteSkill(managedRoot, "mohist", "managed mohist body");
        WriteSkill(managedRoot, "mohist-explore", "managed explore body");
        SkillAssetManifest.Write(
            managedRoot,
            SkillAssetManifest.ResolveCurrentBuildIdentity(),
            new[] { "mohist", "mohist-explore" },
            _files);
        using var stdout = new StringWriter();
        using var jsonStdout = new StringWriter();
        var resolver = new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => userHome);
        var assets = new SkillAssetService(_files, _environment, resolver);

        var exitCode = await BuildRootCommand(stdout, assets: assets).Parse(["skills", "path", "mohist"]).InvokeAsync();
        var jsonExitCode = await BuildRootCommand(jsonStdout, assets: assets).Parse(["skills", "path", "mohist", "--json"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(0, jsonExitCode);
        var textPath = stdout.ToString().Trim();
        Assert.Equal(Path.Combine(managedRoot, "mohist"), textPath);
        var item = JsonNode.Parse(jsonStdout.ToString())!.AsObject();
        Assert.Equal("mohist", item["name"]!.GetValue<string>());
        Assert.Equal(Path.Combine(managedRoot, "mohist"), item["path"]!.GetValue<string>());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task Get_ReturnsFullPackagedGuidance_FromManagedCache_WhenSelected()
    {
        var userHome = Path.Combine("/tmp", $"mohist-content-home-{Guid.NewGuid():N}");
        var managedRoot = Path.Combine(userHome, ".mohist", "cli", "skill-data");
        _files.AddDirectory(managedRoot);
        WriteSkill(managedRoot, "mohist", "managed mohist body");
        WriteSkill(managedRoot, "mohist-explore", "managed explore body");
        SkillAssetManifest.Write(
            managedRoot,
            SkillAssetManifest.ResolveCurrentBuildIdentity(),
            new[] { "mohist", "mohist-explore" },
            _files);
        var resolver = new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => userHome);
        var assets = new SkillAssetService(_files, _environment, resolver);
        using var mohistStdout = new StringWriter();
        using var exploreStdout = new StringWriter();

        var mohistExit = await BuildRootCommand(mohistStdout, assets: assets).Parse(["skills", "get", "mohist"]).InvokeAsync();
        var exploreExit = await BuildRootCommand(exploreStdout, assets: assets).Parse(["skills", "get", "mohist-explore"]).InvokeAsync();

        Assert.Equal(0, mohistExit);
        Assert.Equal(0, exploreExit);
        Assert.Contains("name: mohist", mohistStdout.ToString());
        Assert.Contains("name: mohist-explore", exploreStdout.ToString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task Path_FallsBackToSiblingRoot_WhenManagedCacheIsAbsent()
    {
        var siblingRoot = Path.Combine(AppContext.BaseDirectory, "skill-data");
        _files.AddDirectory(siblingRoot);
        var sourceRoot = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "cli", "Mohist.Cli", "skill-data");
        if (Directory.Exists(sourceRoot))
        {
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                _files.AddFile(Path.Combine(siblingRoot, relative), File.ReadAllText(file));
            }
        }
        SkillAssetManifest.Write(
            siblingRoot,
            SkillAssetManifest.ResolveCurrentBuildIdentity(),
            new[] { "mohist", "mohist-explore" },
            _files);

        using var stdout = new StringWriter();
        var resolver = new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => Path.Combine("/tmp", $"empty-home-{Guid.NewGuid():N}"));
        var assets = new SkillAssetService(_files, _environment, resolver);

        var exitCode = await BuildRootCommand(stdout, assets: assets).Parse(["skills", "path", "mohist"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var textPath = stdout.ToString().Trim();
        Assert.Equal(Path.Combine(siblingRoot, "mohist"), textPath);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task Get_FailsWithRepairGuidance_WhenManagedCacheIsIncompatible()
    {
        var userHome = Path.Combine("/tmp", $"user-home-{Guid.NewGuid():N}");
        var managedRoot = Path.Combine(userHome, ".mohist", "cli", "skill-data");
        _files.AddDirectory(managedRoot);
        WriteSkill(managedRoot, "mohist", "stale mohist body");
        WriteSkill(managedRoot, "mohist-explore", "stale explore body");
        SkillAssetManifest.Write(
            managedRoot,
            new SkillAssetBuildIdentity("0.0.0-stale", "deadbeef"),
            new[] { "mohist", "mohist-explore" },
            _files);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var resolver = new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => userHome);
        var assets = new SkillAssetService(_files, _environment, resolver);

        var exitCode = await BuildRootCommand(stdout, stderr, assets)
            .Parse(["skills", "get", "mohist"]).InvokeAsync();

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var diagnostic = stderr.ToString();
        Assert.Contains("mo update", diagnostic, StringComparison.Ordinal);
        Assert.Contains("scripts/install-mo.sh", diagnostic, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task Commands_UseMohistSkillsDirOverride_ForListGetAndPath()
    {
        var overrideRoot = Path.Combine("/tmp", $"override-assets-{Guid.NewGuid():N}");
        _files.AddDirectory(overrideRoot);
        WriteSkill(overrideRoot, "mohist", "override mohist");
        WriteSkill(overrideRoot, "mohist-explore", "override explore");
        SkillAssetManifest.Write(
            overrideRoot,
            SkillAssetManifest.ResolveCurrentBuildIdentity(),
            new[] { "mohist", "mohist-explore" },
            _files);
        using var listStdout = new StringWriter();
        using var getStdout = new StringWriter();
        using var pathStdout = new StringWriter();

        var resolver = new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: () => overrideRoot,
            getManagedAssetRoot: null,
            getUserHome: () => overrideRoot);
        var assets = new SkillAssetService(_files, _environment, resolver);

        Assert.Equal(0, await BuildRootCommand(listStdout, assets: assets).Parse(["skills", "list"]).InvokeAsync());
        Assert.Equal(0, await BuildRootCommand(getStdout, assets: assets).Parse(["skills", "get", "mohist"]).InvokeAsync());
        Assert.Equal(0, await BuildRootCommand(pathStdout, assets: assets).Parse(["skills", "path", "mohist"]).InvokeAsync());

        var lines = SplitLines(listStdout.ToString());
        Assert.Equal(2, lines.Length);
        Assert.Contains("override mohist", getStdout.ToString());
        Assert.Equal(Path.Combine(overrideRoot, "mohist"), pathStdout.ToString().Trim());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Theory]
    [InlineData("get")]
    [InlineData("path")]
    public async Task UnknownSkill_PrintsClearError_AndExitsNonZero(string command)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await BuildRootCommand(stdout, stderr).Parse(["skills", command, "unknown-skill"]).InvokeAsync();

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("Unknown Mohist built-in skill 'unknown-skill'.", stderr.ToString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public async Task ContentCommands_DoNotTouchDotMohistSkills()
    {
        var mohistSkillsDir = Path.Combine("/tmp", $"mohist-skills-content-runtime-{Guid.NewGuid():N}", ".mohist", "skills");
        _files.AddDirectory(mohistSkillsDir);
        var sentinel = Path.Combine(mohistSkillsDir, "sentinel.txt");
        _files.AddFile(sentinel, "keep");

        Assert.Equal(0, await BuildRootCommand().Parse(["skills", "list"]).InvokeAsync());
        Assert.Equal(0, await BuildRootCommand().Parse(["skills", "get", "mohist"]).InvokeAsync());
        Assert.Equal(0, await BuildRootCommand().Parse(["skills", "path", "mohist"]).InvokeAsync());
        Assert.Equal("keep", _files.ReadAllText(sentinel));
    }

    private global::System.CommandLine.RootCommand BuildRootCommand(
        TextWriter? output = null,
        TextWriter? error = null,
        SkillAssetService? assets = null)
    {
        output ??= TextWriter.Null;
        error ??= TextWriter.Null;
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(new HttpClient(), output, error, _files, new SystemCommandExecutor()));
        services.AddSingleton(output);
        services.AddSingleton(error);
        services.AddSingleton<IFileSystem>(_files);
        services.AddSingleton<ICommandExecutor>(new SystemCommandExecutor());
        services.AddSingleton<IEnvironmentVariableProvider>(_environment);
        services.AddSingleton<SystemdServiceInstaller>();
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton(assets ?? BuildDefaultService());
        services.AddSingleton<SkillInstallService>(_ => new SkillInstallService(
            _.GetRequiredService<SkillAssetService>(),
            _.GetRequiredService<IFileSystem>(),
            _.GetRequiredService<IEnvironmentVariableProvider>(),
            output,
            error));

        var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<MohistCliApi>();
        return MohistCliCommands.Build(api, provider);
    }

    private SkillAssetService BuildDefaultService()
    {
        var resolver = new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: () => _defaultAssetRoot,
            getManagedAssetRoot: null,
            getUserHome: () => _defaultAssetRoot);
        return new SkillAssetService(_files, _environment, resolver);
    }

    private string PopulateDefaultAssets()
    {
        var sourceRoot = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "cli", "Mohist.Cli", "skill-data");
        sourceRoot = Path.GetFullPath(sourceRoot);
        var targetRoot = Path.Combine("/tmp", $"mohist-content-defaults-{Guid.NewGuid():N}", "skill-data");
        _files.AddDirectory(targetRoot);

        if (Directory.Exists(sourceRoot))
        {
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                var target = Path.Combine(targetRoot, relative);
                _files.AddFile(target, File.ReadAllText(file));
            }
        }

        SkillAssetManifest.Write(
            targetRoot,
            SkillAssetManifest.ResolveCurrentBuildIdentity(),
            new[] { "mohist", "mohist-explore" },
            _files);
        return targetRoot;
    }

    private string SetUpManagedCache()
    {
        var managedRoot = Path.Combine("/tmp", $"mohist-skills-content-managed-{Guid.NewGuid():N}", ".mohist", "cli", "skill-data");
        _files.AddDirectory(managedRoot);
        WriteSkill(managedRoot, "mohist", "managed mohist body");
        WriteSkill(managedRoot, "mohist-explore", "managed explore body");
        SkillAssetManifest.Write(
            managedRoot,
            SkillAssetManifest.ResolveCurrentBuildIdentity(),
            new[] { "mohist", "mohist-explore" },
            _files);
        return managedRoot;
    }

    private static string[] SplitLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private void WriteSkill(string root, string name, string heading)
    {
        _files.AddDirectory(Path.Combine(root, name));
        _files.AddFile(
            Path.Combine(root, name, "SKILL.md"),
            $"---\nname: {name}\ndescription: {DescriptionFor(name)}\n---\n\n# {heading}\n");
    }

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "从产品和用户视角探索 mohist 项目，发现功能缺陷、体验问题、设计机会和价值增长点。当用户想要探索代码库、发现改进点、审查用户体验、思考功能设计、或无目标地巡检产品时使用。触发词包括 \"explore\"、\"探索\"、\"巡检\"、\"找问题\"、\"体验审查\"、\"功能设计\"、\"产品思考\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private sealed class SkillListItem
    {
        public string? Name { get; init; }
        public string? Description { get; init; }
    }

}
