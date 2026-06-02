using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("SkillsCli")]
public sealed class SkillsContentSpecs : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mohist-skills-content-{Guid.NewGuid():N}");
    private readonly string _originalDirectory;

    public SkillsContentSpecs()
    {
        _originalDirectory = TryGetCurrentDirectory();
        Directory.CreateDirectory(_tempRoot);
        Directory.SetCurrentDirectory(_tempRoot);
    }

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

    [Fact]
    public async Task Get_PrintsPackagedFullGuidance_NotInstalledStub()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".agents", "skills", "mohist"));
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, ".agents", "skills", "mohist", "SKILL.md"), "stub");
        var original = Directory.GetCurrentDirectory();
        using var stdout = new StringWriter();

        try
        {
            Directory.SetCurrentDirectory(_tempRoot);
            var exitCode = await BuildRootCommand(stdout).Parse(["skills", "get", "mohist"]).InvokeAsync();

            Assert.Equal(0, exitCode);
            var content = stdout.ToString();
            Assert.Contains("name: mohist", content);
            Assert.DoesNotContain("stub", content);
            Assert.DoesNotContain("Run `mo skills get mohist`", content);
            Assert.DoesNotContain("mo skills get mohist --full", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(original))
                Directory.SetCurrentDirectory(original);
        }
    }

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

    [Fact]
    public async Task Path_PrintsManagedCachePath_WhenManagedCacheIsSelected()
    {
        var managedRoot = SetUpManagedCache(_tempRoot);
        using var stdout = new StringWriter();
        using var jsonStdout = new StringWriter();
        var resolver = new SkillAssetRootResolver(
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => _tempRoot);
        var assets = new SkillAssetService(resolver);

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

    [Fact]
    public async Task Get_ReturnsFullPackagedGuidance_FromManagedCache_WhenSelected()
    {
        _ = SetUpManagedCache(_tempRoot);
        var resolver = new SkillAssetRootResolver(
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => _tempRoot);
        var assets = new SkillAssetService(resolver);
        using var mohistStdout = new StringWriter();
        using var exploreStdout = new StringWriter();

        var mohistExit = await BuildRootCommand(mohistStdout, assets: assets).Parse(["skills", "get", "mohist"]).InvokeAsync();
        var exploreExit = await BuildRootCommand(exploreStdout, assets: assets).Parse(["skills", "get", "mohist-explore"]).InvokeAsync();

        Assert.Equal(0, mohistExit);
        Assert.Equal(0, exploreExit);
        Assert.Contains("name: mohist", mohistStdout.ToString());
        Assert.Contains("name: mohist-explore", exploreStdout.ToString());
    }

    [Fact]
    public async Task Path_FallsBackToSiblingRoot_WhenManagedCacheIsAbsent()
    {
        using var stdout = new StringWriter();
        var resolver = new SkillAssetRootResolver(
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => Path.Combine(_tempRoot, "empty-home"));
        var assets = new SkillAssetService(resolver);

        var exitCode = await BuildRootCommand(stdout, assets: assets).Parse(["skills", "path", "mohist"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var textPath = stdout.ToString().Trim();
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "skill-data", "mohist"), textPath);
    }

    [Fact]
    public async Task Get_FailsWithRepairGuidance_WhenManagedCacheIsIncompatible()
    {
        var managedRoot = Path.Combine(_tempRoot, "user-home", ".mohist", "cli", "skill-data");
        WriteSkill(managedRoot, "mohist", "stale mohist body");
        WriteSkill(managedRoot, "mohist-explore", "stale explore body");
        SkillAssetManifest.Write(
            managedRoot,
            new SkillAssetBuildIdentity("0.0.0-stale", "deadbeef"),
            new[] { "mohist", "mohist-explore" });

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var resolver = new SkillAssetRootResolver(
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => Path.Combine(_tempRoot, "user-home"));
        var assets = new SkillAssetService(resolver);

        var exitCode = await BuildRootCommand(stdout, stderr, assets)
            .Parse(["skills", "get", "mohist"]).InvokeAsync();

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var diagnostic = stderr.ToString();
        Assert.Contains("mo update", diagnostic, StringComparison.Ordinal);
        Assert.Contains("scripts/install-mo.sh", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_UseMohistSkillsDirOverride_ForListGetAndPath()
    {
        var overrideRoot = Path.Combine(_tempRoot, "override-assets");
        WriteSkill(overrideRoot, "mohist", "override mohist");
        WriteSkill(overrideRoot, "mohist-explore", "override explore");
        SkillAssetManifest.Write(
            overrideRoot,
            SkillAssetManifest.ResolveCurrentBuildIdentity(),
            new[] { "mohist", "mohist-explore" });
        using var listStdout = new StringWriter();
        using var getStdout = new StringWriter();
        using var pathStdout = new StringWriter();

        try
        {
            Environment.SetEnvironmentVariable("MOHIST_SKILLS_DIR", overrideRoot);

            Assert.Equal(0, await BuildRootCommand(listStdout).Parse(["skills", "list"]).InvokeAsync());
            Assert.Equal(0, await BuildRootCommand(getStdout).Parse(["skills", "get", "mohist"]).InvokeAsync());
            Assert.Equal(0, await BuildRootCommand(pathStdout).Parse(["skills", "path", "mohist"]).InvokeAsync());

            var lines = SplitLines(listStdout.ToString());
            Assert.Equal(2, lines.Length);
            Assert.Contains("override mohist", getStdout.ToString());
            Assert.Equal(Path.Combine(overrideRoot, "mohist"), pathStdout.ToString().Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOHIST_SKILLS_DIR", null);
        }
    }

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

    [Fact]
    public async Task ContentCommands_DoNotTouchDotMohistSkills()
    {
        var mohistSkillsDir = Path.Combine(_tempRoot, ".mohist", "skills");
        Directory.CreateDirectory(mohistSkillsDir);
        var sentinel = Path.Combine(mohistSkillsDir, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var original = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(_tempRoot);
            Assert.Equal(0, await BuildRootCommand().Parse(["skills", "list"]).InvokeAsync());
            Assert.Equal(0, await BuildRootCommand().Parse(["skills", "get", "mohist"]).InvokeAsync());
            Assert.Equal(0, await BuildRootCommand().Parse(["skills", "path", "mohist"]).InvokeAsync());
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            if (Directory.Exists(original))
                Directory.SetCurrentDirectory(original);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_originalDirectory))
            Directory.SetCurrentDirectory(_originalDirectory);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static System.CommandLine.RootCommand BuildRootCommand(
        TextWriter? output = null,
        TextWriter? error = null,
        SkillAssetService? assets = null)
    {
        output ??= TextWriter.Null;
        error ??= TextWriter.Null;
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(new HttpClient(), output, error, RealFileSystem.Instance, new SystemCommandExecutor()));
        services.AddSingleton(output);
        services.AddSingleton(error);
        services.AddSingleton<IFileSystem>(RealFileSystem.Instance);
        services.AddSingleton<ICommandExecutor>(new SystemCommandExecutor());
        services.AddSingleton<SystemdServiceInstaller>();
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton(assets ?? new SkillAssetService());
        services.AddSingleton<SkillInstallService>();

        var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<MohistCliApi>();
        return MohistCliCommands.Build(api, provider);
    }

    private static string SetUpManagedCache(string homeDirectory)
    {
        var managedRoot = Path.Combine(homeDirectory, ".mohist", "cli", "skill-data");
        WriteSkill(managedRoot, "mohist", "managed mohist body");
        WriteSkill(managedRoot, "mohist-explore", "managed explore body");
        SkillAssetManifest.Write(
            managedRoot,
            SkillAssetManifest.ResolveCurrentBuildIdentity(),
            new[] { "mohist", "mohist-explore" });
        return managedRoot;
    }

    private static string[] SplitLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string TryGetCurrentDirectory()
    {
        try
        {
            var current = Directory.GetCurrentDirectory();
            return Directory.Exists(current) ? current : Path.GetTempPath();
        }
        catch (IOException)
        {
            return Path.GetTempPath();
        }
    }

    private static void WriteSkill(string root, string name, string heading)
    {
        Directory.CreateDirectory(Path.Combine(root, name));
        File.WriteAllText(
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
