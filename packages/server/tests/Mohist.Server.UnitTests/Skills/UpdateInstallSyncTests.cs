using Mohist.Server.UnitTests.Support;
using System.Net;
using Mohist.Cli;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.UnitTests.Skills;

public sealed class UpdateInstallSyncTests
{
    private readonly FakeFileSystem _files = new();
    private readonly MockEnvironmentVariableProvider _environment = new();

    public UpdateInstallSyncTests()
    {
        _environment[SkillAssetRootResolver.OverrideEnvironmentVariable] = null;
    }

    [Fact]
    public async Task UpdateCliAsync_SynchronizesPublishedSkillData_IntoManagedCacheWithBuiltInSkills()
    {
        var tempRoot = NewIsolatedRoot("update-basic");
        WritePackagedSkillAssets(tempRoot);

        var commands = new FakeCommandExecutor();
        var (updater, _) = BuildUpdater(commands, tempRoot);

        var exitCode = await updater.UpdateCliAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(0, exitCode);
        var managedRoot = Path.Combine(tempRoot, ".mohist", "cli", "skill-data");
        Assert.True(_files.HasFile(Path.Combine(managedRoot, "mohist", "SKILL.md")));
        Assert.True(_files.HasFile(Path.Combine(managedRoot, "mohist-explore", "SKILL.md")));
    }

    [Fact]
    public async Task UpdateCliAsync_ReplacesStaleManagedCacheContents_WithCurrentPublishOutput()
    {
        var tempRoot = NewIsolatedRoot("update-stale");
        WritePackagedSkillAssets(tempRoot);

        var managedRoot = Path.Combine(tempRoot, ".mohist", "cli", "skill-data");
        _files.AddDirectory(Path.Combine(managedRoot, "mohist"));
        _files.AddDirectory(Path.Combine(managedRoot, "stale-skill"));
        _files.AddFile(
            Path.Combine(managedRoot, "mohist", "SKILL.md"),
            "---\nname: mohist\ndescription: STALE\n---\n\n# STALE\n");
        _files.AddFile(Path.Combine(managedRoot, "stale-skill", "SKILL.md"), "STALE SKILL");
        _files.AddFile(Path.Combine(managedRoot, "stale.txt"), "stale-marker");

        var commands = new FakeCommandExecutor();
        var (updater, _) = BuildUpdater(commands, tempRoot);

        var exitCode = await updater.UpdateCliAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(0, exitCode);
        Assert.False(_files.HasFile(Path.Combine(managedRoot, "stale.txt")));
        Assert.False(_files.DirectoryExists(Path.Combine(managedRoot, "stale-skill")));
        Assert.True(_files.HasFile(Path.Combine(managedRoot, "mohist", "SKILL.md")));
        Assert.True(_files.HasFile(Path.Combine(managedRoot, "mohist-explore", "SKILL.md")));

        var mohistBody = _files.ReadAllText(Path.Combine(managedRoot, "mohist", "SKILL.md"));
        Assert.DoesNotContain("STALE", mohistBody);
    }

    [Fact]
    public async Task UpdateCliAsync_DoesNotModifyExternalAgentSkillDirectories()
    {
        var tempRoot = NewIsolatedRoot("update-external-dirs");
        WritePackagedSkillAssets(tempRoot);

        var agentsSkillDir = Path.Combine(tempRoot, ".agents", "skills", "mohist-po");
        var claudeSkillDir = Path.Combine(tempRoot, ".claude", "skills", "user-skill");
        var hermesSkillDir = Path.Combine(tempRoot, ".hermes", "skills", "user-skill");
        var hermesConfig = Path.Combine(tempRoot, ".hermes", "config.yaml");
        _files.AddDirectory(agentsSkillDir);
        _files.AddDirectory(claudeSkillDir);
        _files.AddDirectory(hermesSkillDir);
        _files.AddFile(Path.Combine(agentsSkillDir, "SKILL.md"), "external-agent-skill");
        _files.AddFile(Path.Combine(claudeSkillDir, "SKILL.md"), "external-claude-skill");
        _files.AddFile(Path.Combine(hermesSkillDir, "SKILL.md"), "external-hermes-skill");
        _files.AddFile(hermesConfig, "skills:\n  external_dirs: []\n");

        var commands = new FakeCommandExecutor();
        var (updater, _) = BuildUpdater(commands, tempRoot);

        var exitCode = await updater.UpdateCliAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "external-agent-skill",
            _files.ReadAllText(Path.Combine(agentsSkillDir, "SKILL.md")));
        Assert.Equal(
            "external-claude-skill",
            _files.ReadAllText(Path.Combine(claudeSkillDir, "SKILL.md")));
        Assert.Equal(
            "external-hermes-skill",
            _files.ReadAllText(Path.Combine(hermesSkillDir, "SKILL.md")));
        Assert.Equal(
            "skills:\n  external_dirs: []\n",
            _files.ReadAllText(hermesConfig));
    }

    [Fact]
    public async Task UpdateCliAsync_DoesNotModifyRuntimeMohistSkillsDirectory()
    {
        var tempRoot = NewIsolatedRoot("update-runtime-mohist-skills");
        WritePackagedSkillAssets(tempRoot);

        var runtimeSkillsDir = Path.Combine(tempRoot, ".mohist", "skills");
        _files.AddDirectory(runtimeSkillsDir);
        var sentinelPath = Path.Combine(runtimeSkillsDir, "sentinel.txt");
        var nestedSkillPath = Path.Combine(runtimeSkillsDir, "internal-skill", "SKILL.md");
        _files.AddFile(sentinelPath, "runtime-sentinel");
        _files.AddDirectory(Path.GetDirectoryName(nestedSkillPath)!);
        _files.AddFile(nestedSkillPath, "internal-skill-body");

        var commands = new FakeCommandExecutor();
        var (updater, _) = BuildUpdater(commands, tempRoot);

        var exitCode = await updater.UpdateCliAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(0, exitCode);
        Assert.Equal("runtime-sentinel", _files.ReadAllText(sentinelPath));
        Assert.Equal("internal-skill-body", _files.ReadAllText(nestedSkillPath));
    }

    [Fact]
    public async Task UpdateCliAsync_EnablesSkillAssetServiceResolution_WithoutMohistSkillsDirOverride()
    {
        var tempRoot = NewIsolatedRoot("update-resolution");
        WritePackagedSkillAssets(tempRoot);

        var commands = new FakeCommandExecutor();
        var (updater, files) = BuildUpdater(commands, tempRoot);

        var exitCode = await updater.UpdateCliAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo");
        Assert.Equal(0, exitCode);

        Assert.Null(_environment.GetEnvironmentVariable(SkillAssetRootResolver.OverrideEnvironmentVariable));

        var resolver = new SkillAssetRootResolver(
            files,
            new MockEnvironmentVariableProvider(),
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => tempRoot);
        var service = new SkillAssetService(files, _environment, resolver);

        Assert.Equal(SkillAssetRootSource.ManagedCache, service.AssetRootSource);
        var expectedManagedRoot = Path.Combine(tempRoot, ".mohist", "cli", "skill-data");
        Assert.Equal(
            expectedManagedRoot,
            service.AssetRoot!);

        var mohistResult = service.GetSkill("mohist", includeSupplementaryFiles: false);
        Assert.True(mohistResult.Found, mohistResult.Error);
        Assert.Equal(
            Path.Combine(expectedManagedRoot, "mohist"),
            mohistResult.Skill!.DirectoryPath);

        var exploreResult = service.GetSkill("mohist-explore", includeSupplementaryFiles: false);
        Assert.True(exploreResult.Found, exploreResult.Error);
        Assert.Equal(
            Path.Combine(expectedManagedRoot, "mohist-explore"),
            exploreResult.Skill!.DirectoryPath);
    }

    private static string NewIsolatedRoot(string label) =>
        Path.Combine(
            "/mohist-tests/update-install-sync",
            label);

    private void WritePackagedSkillAssets(string tempRoot)
    {
        var publishSource = Path.Combine(tempRoot, ".publish", "cli", "skill-data");
        _files.AddDirectory(Path.Combine(publishSource, "mohist"));
        _files.AddDirectory(Path.Combine(publishSource, "mohist-explore"));
        _files.AddFile(
            Path.Combine(publishSource, "mohist", "SKILL.md"),
            BuildSkillMarkdown("mohist"));
        _files.AddFile(
            Path.Combine(publishSource, "mohist-explore", "SKILL.md"),
            BuildSkillMarkdown("mohist-explore"));
    }

    private static string BuildSkillMarkdown(string name) =>
        $"---\nname: {name}\ndescription: {DescriptionFor(name)}\n---\n\n# {name}\n";

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue 或 epic，查看项目状态或日志，或任何涉及 Mohist issue/epic/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。当用户带着一句话、一个模糊念头或未沉淀的改进意图，需要探索当前产品形态和技术实现，最终产出一份用户视角、产品视角、领域视角三段协作的 PRD 时使用。触发词包括 \"提炼需求\"、\"写 PRD\"、\"沉淀 issue\"、\"需求文档\"、\"探索\"、\"完善 issue\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private (SourceCodeUpdater Updater, FakeFileSystem Files) BuildUpdater(FakeCommandExecutor commands, string tempRoot)
    {
        var files = _files;
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new ConstantStatusHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
        return (updater, files);
    }

    private sealed class FakeCommandExecutor : ICommandExecutor
    {
        public readonly List<(string FileName, string[] Args, string? WorkingDirectory)> ExecutedCommands = new();

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            ExecutedCommands.Add((fileName, args, workingDirectory));
            return Task.FromResult((0, string.Empty, string.Empty));
        }
    }

    private sealed class ConstantStatusHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public ConstantStatusHttpHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("<html></html>"),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            return Task.FromResult(response);
        }
    }
}
