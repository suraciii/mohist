using System.Net;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Specs;

[Collection("SkillsCli")]
public sealed class UpdateInstallSyncSpecs
{
    private readonly FakeFileSystem _files = new();
    private readonly MockEnvironmentVariableProvider _environment = new();

    public UpdateInstallSyncSpecs()
    {
        _environment[SkillAssetRootResolver.OverrideEnvironmentVariable] = null;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task UpdateCliAsync_SynchronizesPublishedSkillData_IntoManagedCacheWithManifestAndBuiltInSkills()
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
        Assert.True(_files.HasFile(Path.Combine(managedRoot, "manifest.json")));
        Assert.True(_files.HasFile(Path.Combine(managedRoot, "mohist", "SKILL.md")));
        Assert.True(_files.HasFile(Path.Combine(managedRoot, "mohist-explore", "SKILL.md")));

        var readManifest = SkillAssetManifest.TryRead(managedRoot, _files);
        Assert.True(readManifest.IsFound);
        Assert.Contains("mohist", readManifest.Data!.Skills);
        Assert.Contains("mohist-explore", readManifest.Data.Skills);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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
        SkillAssetManifest.Write(
            managedRoot,
            new SkillAssetBuildIdentity("0.0.0-stale", "deadbeef"),
            new[] { "mohist", "mohist-explore", "stale-skill" },
            _files);

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

        var manifest = SkillAssetManifest.TryRead(managedRoot, _files);
        Assert.True(manifest.IsFound);
        Assert.DoesNotContain("stale-skill", manifest.Data!.Skills);
        Assert.NotEqual("0.0.0-stale", manifest.Data.Version);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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
            getUserHome: () => tempRoot,
            getBuildIdentity: SkillAssetManifest.ResolveCurrentBuildIdentity);
        var service = new SkillAssetService(files, _environment, resolver);

        Assert.Equal(SkillAssetRootSource.ManagedCache, service.AssetRootSource);
        var expectedManagedRoot = Path.Combine(tempRoot, ".mohist", "cli", "skill-data");
        Assert.Equal(
            Path.GetFullPath(expectedManagedRoot),
            Path.GetFullPath(service.AssetRoot!));

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

    private string NewIsolatedRoot(string label) => Path.Combine("/tmp", $"mohist-update-sync-{label}-{Guid.NewGuid():N}");

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
        var identity = SkillAssetManifest.ResolveCurrentBuildIdentity();
        SkillAssetManifest.Write(
            publishSource,
            identity,
            new[] { "mohist", "mohist-explore" },
            _files);
    }

    private static string BuildSkillMarkdown(string name) =>
        $"---\nname: {name}\ndescription: {DescriptionFor(name)}\n---\n\n# {name}\n";

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "从产品和用户视角探索 mohist 项目，发现功能缺陷、体验问题、设计机会和价值增长点。当用户想要探索代码库、发现改进点、审查用户体验、思考功能设计、或无目标地巡检产品时使用。触发词包括 \"explore\"、\"探索\"、\"巡检\"、\"找问题\"、\"体验审查\"、\"功能设计\"、\"产品思考\"。",
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
        var updater = new SourceCodeUpdater(
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
            string fileName, string[] args, string? workingDirectory = null)
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
