using Mohist.Cli.Tests.Compatibility;
using global::System.Text.Json;
using global::System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mohist.Cli;
using CliCompositionTestFactory = Mohist.Cli.Tests.Support.CliCompositionTestFactory;
using EnvironmentAbstractions.TestHelpers;
using Xunit;

namespace Mohist.Cli.Tests.Skills;

public sealed class SkillsContentTests
{
    private static readonly Regex FencedMoCommand = new("^\\s*(mo\\s+.+?)(?:\\s+#.*)?$", RegexOptions.Compiled);
    private static readonly Regex InlineMoCommand = new("`(mo\\s+[^`]+)`", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FakeFileSystem _files = new();
    private readonly MockEnvironmentVariableProvider _environment = new();
    private readonly string _defaultAssetRoot;

    public SkillsContentTests()
    {
        _defaultAssetRoot = PopulateDefaultAssets();
    }

    [Fact]
    public async Task List_PrintsVisibleBuiltInSkillsSortedByName()
    {
        using var stdout = new StringWriter();

        var exitCode = await BuildRootCommand(stdout).Parse(["skill", "list"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var lines = SplitLines(stdout.ToString());
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("mohist\t", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("mohist-create-epic\t", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("mohist-create-issue\t", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("mohist-explore\t", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_Json_ReturnsStructuredSortedEntries()
    {
        using var stdout = new StringWriter();

        var exitCode = await BuildRootCommand(stdout).Parse(["skill", "list", "--json", "name,description"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var items = JsonSerializer.Deserialize<List<SkillListItem>>(stdout.ToString(), JsonOptions);
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
                Assert.Equal("mohist-create-epic", item.Name);
                Assert.False(string.IsNullOrWhiteSpace(item.Description));
            },
            item =>
            {
                Assert.Equal("mohist-create-issue", item.Name);
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
        var agentsStubDir = Path.Combine("/mohist-tests", $"agents-stubs-{Guid.NewGuid():N}", ".agents", "skills", "mohist");
        _files.AddDirectory(agentsStubDir);
        _files.AddFile(Path.Combine(agentsStubDir, "SKILL.md"), "stub");

        using var stdout = new StringWriter();

        var exitCode = await BuildRootCommand(stdout).Parse(["skill", "view", "mohist"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var content = stdout.ToString();
        Assert.Contains("name: mohist", content);
        Assert.DoesNotContain("stub", content);
        Assert.DoesNotContain("Run `mo skills get mohist`", content);
        Assert.DoesNotContain("mo skills get mohist --full", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Json_ReturnsNameDescriptionAndContent()
    {
        using var stdout = new StringWriter();

        var exitCode = await BuildRootCommand(stdout).Parse(["skill", "view", "mohist", "--json", "name,description,content"]).InvokeAsync();

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

        var exitCode = await BuildRootCommand(stdout).Parse(["skill", "view", "mohist-create-epic", "--full"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var content = stdout.ToString();
        var marker = "--- references/epic-templates.md ---";
        Assert.Contains(marker, content);
        Assert.True(content.IndexOf(marker, StringComparison.Ordinal) > content.IndexOf("name: mohist-create-epic", StringComparison.Ordinal));
        Assert.DoesNotContain("mo skills get mohist --full", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_All_EmitsAllVisibleBuiltInSkillsInNameOrder()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await BuildRootCommand(stdout, stderr).Parse(["skill", "view", "--all"]).InvokeAsync();

        Assert.True(exitCode == 0, $"stderr: {stderr}");
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

        var exitCode = await BuildRootCommand(stdout).Parse(["skill", "path", "mohist"]).InvokeAsync();
        var jsonExitCode = await BuildRootCommand(jsonStdout).Parse(["skill", "path", "mohist", "--json", "name,path"]).InvokeAsync();

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
        var userHome = Path.Combine("/mohist-tests", $"mohist-content-home-{Guid.NewGuid():N}");
        var managedRoot = Path.Combine(userHome, ".mohist", "cli", "skill-data");
        _files.AddDirectory(managedRoot);
        WriteSkill(managedRoot, "mohist", "managed mohist body");
        WriteSkill(managedRoot, "mohist-explore", "managed explore body");
        using var stdout = new StringWriter();
        using var jsonStdout = new StringWriter();
        var resolver = new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => userHome);
        var assets = new SkillAssetService(_files, _environment, resolver);

        var exitCode = await BuildRootCommand(stdout, assets: assets).Parse(["skill", "path", "mohist"]).InvokeAsync();
        var jsonExitCode = await BuildRootCommand(jsonStdout, assets: assets).Parse(["skill", "path", "mohist", "--json", "name,path"]).InvokeAsync();

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
        var userHome = Path.Combine("/mohist-tests", $"mohist-content-home-{Guid.NewGuid():N}");
        var managedRoot = Path.Combine(userHome, ".mohist", "cli", "skill-data");
        _files.AddDirectory(managedRoot);
        WriteSkill(managedRoot, "mohist", "managed mohist body");
        WriteSkill(managedRoot, "mohist-explore", "managed explore body");
        var resolver = new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => userHome);
        var assets = new SkillAssetService(_files, _environment, resolver);
        using var mohistStdout = new StringWriter();
        using var exploreStdout = new StringWriter();

        var mohistExit = await BuildRootCommand(mohistStdout, assets: assets).Parse(["skill", "view", "mohist"]).InvokeAsync();
        var exploreExit = await BuildRootCommand(exploreStdout, assets: assets).Parse(["skill", "view", "mohist-explore"]).InvokeAsync();

        Assert.Equal(0, mohistExit);
        Assert.Equal(0, exploreExit);
        Assert.Contains("name: mohist", mohistStdout.ToString());
        Assert.Contains("name: mohist-explore", exploreStdout.ToString());
    }

    [Fact]
    public async Task Path_FallsBackToSiblingRoot_WhenManagedCacheIsAbsent()
    {
        var siblingRoot = "/mohist-tests/sibling-skill-data";
        _files.AddDirectory(siblingRoot);
        WriteSkill(siblingRoot, "mohist", "sibling mohist body");
        WriteSkill(siblingRoot, "mohist-explore", "sibling explore body");

        using var stdout = new StringWriter();
        var resolver = new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => Path.Combine("/mohist-tests", $"empty-home-{Guid.NewGuid():N}"),
            getSiblingAssetRoot: () => siblingRoot);
        var assets = new SkillAssetService(_files, _environment, resolver);

        var exitCode = await BuildRootCommand(stdout, assets: assets).Parse(["skill", "path", "mohist"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        var textPath = stdout.ToString().Trim();
        Assert.Equal(Path.Combine(siblingRoot, "mohist"), textPath);
    }

    [Fact]
    public async Task Get_FailsWithRepairGuidance_WhenNoAssetRootIsResolvable()
    {
        var userHome = Path.Combine("/mohist-tests", $"user-home-{Guid.NewGuid():N}");

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
            .Parse(["skill", "view", "mohist"]).InvokeAsync();

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var diagnostic = stderr.ToString();
        Assert.Contains("mo update", diagnostic, StringComparison.Ordinal);
        Assert.Contains("scripts/install-mo.sh", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_UseMohistSkillsDirOverride_ForListGetAndPath()
    {
        var overrideRoot = Path.Combine("/mohist-tests", $"override-assets-{Guid.NewGuid():N}");
        _files.AddDirectory(overrideRoot);
        WriteSkill(overrideRoot, "mohist", "override mohist");
        WriteSkill(overrideRoot, "mohist-explore", "override explore");
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

        Assert.Equal(0, await BuildRootCommand(listStdout, assets: assets).Parse(["skill", "list"]).InvokeAsync());
        Assert.Equal(0, await BuildRootCommand(getStdout, assets: assets).Parse(["skill", "view", "mohist"]).InvokeAsync());
        Assert.Equal(0, await BuildRootCommand(pathStdout, assets: assets).Parse(["skill", "path", "mohist"]).InvokeAsync());

        var lines = SplitLines(listStdout.ToString());
        Assert.Equal(2, lines.Length);
        Assert.Contains("override mohist", getStdout.ToString());
        Assert.Equal(Path.Combine(overrideRoot, "mohist"), pathStdout.ToString().Trim());
    }

    [Theory]
    [InlineData("view")]
    [InlineData("path")]
    public async Task UnknownSkill_PrintsClearError_AndExitsNonZero(string command)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await BuildRootCommand(stdout, stderr).Parse(["skill", command, "unknown-skill"]).InvokeAsync();

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("Unknown Mohist built-in skill 'unknown-skill'.", stderr.ToString());
    }

    [Fact]
    public async Task ContentCommands_DoNotTouchDotMohistSkills()
    {
        var mohistSkillsDir = Path.Combine("/mohist-tests", $"mohist-skills-content-runtime-{Guid.NewGuid():N}", ".mohist", "skills");
        _files.AddDirectory(mohistSkillsDir);
        var sentinel = Path.Combine(mohistSkillsDir, "sentinel.txt");
        _files.AddFile(sentinel, "keep");

        Assert.Equal(0, await BuildRootCommand().Parse(["skill", "list"]).InvokeAsync());
        Assert.Equal(0, await BuildRootCommand().Parse(["skill", "view", "mohist"]).InvokeAsync());
        Assert.Equal(0, await BuildRootCommand().Parse(["skill", "path", "mohist"]).InvokeAsync());
        Assert.Equal("keep", _files.ReadAllText(sentinel));
    }

    [Fact]
    public void PackagedSkillMoExamples_ParseAgainstCanonicalCommandTree()
    {
        var root = BuildRootCommand();
        var examples = EmbeddedSkillData.Paths()
            .Where(path => path.EndsWith("/SKILL.md", StringComparison.Ordinal))
            .SelectMany(path => ExtractMoExamples(path).Select(command => (path, command)))
            .ToArray();

        Assert.NotEmpty(examples);
        foreach (var (path, command) in examples)
        {
            var parseResult = root.Parse(Tokenize(command)
                .Skip(1)
                .Select(token => token.StartsWith('<') && token.EndsWith('>') ? "1" : token)
                .ToArray());
            Assert.True(
                parseResult.Errors.Count == 0,
                $"Packaged Skill example failed to parse: {path}: {command}\n{string.Join("\n", parseResult.Errors.Select(error => error.Message))}");
        }
    }

    [Fact]
    public void PackagedSkillInlineMoExamples_ParseAgainstCanonicalCommandTree()
    {
        var root = BuildRootCommand();
        var examples = EmbeddedSkillData.Paths()
            .Where(path => path.EndsWith("/SKILL.md", StringComparison.Ordinal))
            .SelectMany(ExtractInlineMoExamples)
            .Where(command => !command.Contains("<command>", StringComparison.Ordinal)
                && !command.EndsWith(" --body-file", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("mo workflow list", examples, StringComparer.Ordinal);
        foreach (var command in examples)
        {
            var args = Tokenize(command)
                .Skip(1)
                .Select(token => token.StartsWith('<') && token.EndsWith('>') ? "1" : token)
                .Append("--help")
                .ToArray();
            var parseResult = root.Parse(args);
            Assert.True(
                parseResult.Errors.All(error => error.Message.StartsWith("Required argument missing", StringComparison.Ordinal)),
                $"Packaged inline Skill example failed to parse: {command}\n{string.Join("\n", parseResult.Errors.Select(error => error.Message))}");
        }
    }

    [Fact]
    public void MohistEntrySkill_IsAProgressiveDecisionEntryPoint()
    {
        var content = EmbeddedSkillData.ReadText("mohist/SKILL.md");

        Assert.Contains("## Scope", content, StringComparison.Ordinal);
        Assert.Contains("## First read", content, StringComparison.Ordinal);
        Assert.Contains("## Scenario routing", content, StringComparison.Ordinal);
        Assert.Contains("## Mohist-specific decisions", content, StringComparison.Ordinal);
        Assert.Contains("## CLI handoff", content, StringComparison.Ordinal);
        Assert.Contains("mo issue view <number>", content, StringComparison.Ordinal);
        Assert.Contains("mo run view <run-id>", content, StringComparison.Ordinal);
        Assert.Contains("mohist-explore", content, StringComparison.Ordinal);
        Assert.Contains("mohist-create-issue", content, StringComparison.Ordinal);
        Assert.Contains("mohist-create-epic", content, StringComparison.Ordinal);
        Assert.Contains("retry", content, StringComparison.Ordinal);
        Assert.Contains("rerun --from-stage", content, StringComparison.Ordinal);
        Assert.Contains("pause", content, StringComparison.Ordinal);
        Assert.Contains("stop", content, StringComparison.Ordinal);
        Assert.Contains("compact", content, StringComparison.Ordinal);
        Assert.Contains("reset", content, StringComparison.Ordinal);

        foreach (var forbidden in new[]
        {
            "dotnet ", "npm ", "packages/", "legacy", "pre-Orleans", "mo issue show",
            "mo skills", "mo repository", "mo opencode", "mo config", "| Operation |",
        })
        {
            Assert.DoesNotContain(forbidden, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("skills", "list")]
    [InlineData("repository", "view", "repo")]
    [InlineData("opencode", "models")]
    [InlineData("config")]
    [InlineData("skill", "get", "mohist")]
    [InlineData("epic", "show", "1")]
    public void RemovedSkillPaths_FailTheSameParserCheck(params string[] args)
    {
        var parseResult = BuildRootCommand().Parse(args);

        Assert.NotEmpty(parseResult.Errors);
    }

    private static IEnumerable<string> ExtractMoExamples(string path)
    {
        var inFence = false;
        foreach (var line in EmbeddedSkillData.ReadText(path).Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
                continue;

            var match = FencedMoCommand.Match(line.TrimEnd('\r'));
            if (match.Success)
                yield return match.Groups[1].Value.Trim();
        }
    }

    private static IEnumerable<string> ExtractInlineMoExamples(string path)
    {
        foreach (Match match in InlineMoCommand.Matches(EmbeddedSkillData.ReadText(path)))
            yield return match.Groups[1].Value.Trim();
    }

    private static string[] Tokenize(string command) =>
        Regex.Matches(command, "\\\"(?:\\\\.|[^\\\"])*\\\"|\\S+")
            .Select(match => match.Value.Trim('"'))
            .ToArray();

    private global::System.CommandLine.RootCommand BuildRootCommand(
        TextWriter? output = null,
        TextWriter? error = null,
        SkillAssetService? assets = null)
    {
        output ??= TextWriter.Null;
        error ??= TextWriter.Null;
        return CliCompositionTestFactory.Create(
            _files,
            _environment,
            assets ?? BuildDefaultService(),
            output,
            error).Root;
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
        var targetRoot = Path.Combine("/mohist-tests", $"mohist-content-defaults-{Guid.NewGuid():N}", "skill-data");
        _files.AddDirectory(targetRoot);

        WriteSkill(targetRoot, "mohist", "managed mohist body");
        WriteSkill(targetRoot, "mohist-explore", "managed explore body");
        WriteSkill(targetRoot, "mohist-create-epic", "managed create-epic body");
        WriteSkill(targetRoot, "mohist-create-issue", "managed create-issue body");
        WriteSupplementaryFile(targetRoot, "mohist-create-epic", "references", "epic-templates.md", "# Epic Templates");

        return targetRoot;
    }

    private string SetUpManagedCache()
    {
        var managedRoot = Path.Combine("/mohist-tests", $"mohist-skills-content-managed-{Guid.NewGuid():N}", ".mohist", "cli", "skill-data");
        _files.AddDirectory(managedRoot);
        WriteSkill(managedRoot, "mohist", "managed mohist body");
        WriteSkill(managedRoot, "mohist-explore", "managed explore body");
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

    private void WriteSupplementaryFile(string root, string skillName, string subDir, string fileName, string content)
    {
        var dir = Path.Combine(root, skillName, subDir);
        _files.AddDirectory(dir);
        _files.AddFile(Path.Combine(dir, fileName), content);
    }

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。",
        "mohist-explore" => "把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。",
        "mohist-create-epic" => "驱动 Mohist epic 的核心流程执行。",
        "mohist-create-issue" => "驱动 Mohist issue 的核心流程执行。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private sealed class SkillListItem
    {
        public string? Name { get; init; }
        public string? Description { get; init; }
    }

}
