using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using EnvironmentAbstractions.TestHelpers;
using Xunit;

namespace Mohist.Server.Tests.Specs.Skills;

[Collection("SkillsCli")]
public sealed class SkillsInstallSpecs
{
    private readonly FakeFileSystem _files = new();
    private readonly MockEnvironmentVariableProvider _environment = new();
    private string? _assetRoot;

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Install_DefaultTarget_WritesBuiltInDiscoveryStubsUnderAgentsSkills()
    {
        using var stdout = new StringWriter();
        var assets = BuildDefaultAssetService();

        var exitCode = await BuildRootCommand(stdout, assets: assets)
            .Parse(["skills", "install"])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        var agentsDir = Path.Combine(_files.Cwd, ".agents", "skills", "mohist", "SKILL.md");
        AssertStub(agentsDir, "mohist");
        var exploreDir = Path.Combine(_files.Cwd, ".agents", "skills", "mohist-explore", "SKILL.md");
        AssertStub(exploreDir, "mohist-explore");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Install_PathTarget_WritesOnlyToSelectedRepository()
    {
        var targetRoot = Path.Combine("/tmp", $"skills-install-path-{Guid.NewGuid():N}");
        _files.AddDirectory(targetRoot);
        _files.SetCurrentDirectory(Path.Combine("/tmp", $"skills-install-cwd-{Guid.NewGuid():N}"));

        var assets = BuildDefaultAssetService();
        var exitCode = await BuildRootCommand()
            .Parse(["skills", "install", "--path", targetRoot])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        AssertStub(Path.Combine(targetRoot, ".agents", "skills", "mohist", "SKILL.md"), "mohist");
        Assert.False(_files.HasFile(Path.Combine(_files.Cwd, ".agents", "skills", "mohist", "SKILL.md")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Install_ClaudeTarget_WritesClaudeSkillsOnly()
    {
        _files.SetCurrentDirectory(Path.Combine("/tmp", $"skills-install-claude-{Guid.NewGuid():N}"));

        var assets = BuildDefaultAssetService();
        var exitCode = await BuildRootCommand()
            .Parse(["skills", "install", "--claude"])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        AssertStub(Path.Combine(_files.Cwd, ".claude", "skills", "mohist", "SKILL.md"), "mohist");
        AssertStub(Path.Combine(_files.Cwd, ".claude", "skills", "mohist-explore", "SKILL.md"), "mohist-explore");
        Assert.False(_files.HasFile(Path.Combine(_files.Cwd, ".agents", "skills", "mohist", "SKILL.md")));
        Assert.False(_files.HasFile(Path.Combine(_files.Cwd, ".agents", "skills", "mohist-explore", "SKILL.md")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Install_OverwritesExistingBuiltInStubContent()
    {
        _files.SetCurrentDirectory(Path.Combine("/tmp", $"skills-install-overwrite-{Guid.NewGuid():N}"));
        var skillDir = Path.Combine(_files.Cwd, ".agents", "skills", "mohist");
        _files.AddDirectory(skillDir);
        _files.AddFile(Path.Combine(skillDir, "SKILL.md"), "old content");

        var assets = BuildDefaultAssetService();
        var exitCode = await BuildRootCommand()
            .Parse(["skills", "install"])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        AssertStub(Path.Combine(skillDir, "SKILL.md"), "mohist");
        Assert.DoesNotContain("old content", _files.ReadAllText(Path.Combine(skillDir, "SKILL.md")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Install_DoesNotTouchUnrelatedUserAuthoredSkillDirectories()
    {
        _files.SetCurrentDirectory(Path.Combine("/tmp", $"skills-install-untouched-{Guid.NewGuid():N}"));
        var userSkillDir = Path.Combine(_files.Cwd, ".agents", "skills", "mohist-po");
        _files.AddDirectory(userSkillDir);
        var sentinelPath = Path.Combine(userSkillDir, "SKILL.md");
        _files.AddFile(sentinelPath, "user-authored");

        var assets = BuildDefaultAssetService();
        var exitCode = await BuildRootCommand()
            .Parse(["skills", "install"])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal("user-authored", _files.ReadAllText(sentinelPath));
        Assert.False(_files.DirectoryExists(Path.Combine(_files.Cwd, ".claude", "skills", "mohist-po")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Install_HermesTarget_CopiesFullPackagedSkillData()
    {
        var hermesHome = Path.Combine("/tmp", $"hermes-home-{Guid.NewGuid():N}");
        _environment[SkillInstallService.HermesHomeEnvironmentVariable] = hermesHome;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            var assets = BuildDefaultAssetService();
            var exitCode = await BuildRootCommand(stdout, stderr, assets)
                .Parse(["skills", "install", "--hermes"])
                .InvokeAsync();

            Assert.Equal(0, exitCode);
            var mohistSkill = Path.Combine(hermesHome, "skills", "mohist", "SKILL.md");
            var exploreSkill = Path.Combine(hermesHome, "skills", "mohist-explore", "SKILL.md");
            AssertFullPackagedSkill(mohistSkill, "mohist");
            AssertFullPackagedSkill(exploreSkill, "mohist-explore");
            Assert.True(_files.HasFile(Path.Combine(hermesHome, "skills", "mohist", "references", "issue-templates.md")));
            Assert.False(_files.DirectoryExists(Path.Combine(hermesHome, "skills", "mohist-po")));

            var output = stdout.ToString();
            Assert.Contains("mohist: created", output);
            Assert.Contains("mohist-explore: created", output);
            Assert.Contains("/mohist", output);
            Assert.Contains("/mohist-explore", output);
            Assert.Contains("reload/reset", output);
        }
        finally
        {
            _environment[SkillInstallService.HermesHomeEnvironmentVariable] = null;
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Install_HermesTarget_UsesConfiguredHermesHomeAndReportsUpdatedOnRepeatInstall()
    {
        var hermesHome = Path.Combine("/tmp", $"custom-hermes-home-{Guid.NewGuid():N}");
        var defaultHermesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes", "skills");
        _environment[SkillInstallService.HermesHomeEnvironmentVariable] = hermesHome;

        try
        {
            var assets = BuildDefaultAssetService();

            var firstExit = await BuildRootCommand()
                .Parse(["skills", "install", "--hermes"])
                .InvokeAsync();
            Assert.Equal(0, firstExit);
            using var secondStdout = new StringWriter();
            using var secondStderr = new StringWriter();
            var secondExit = await BuildRootCommand(secondStdout, secondStderr, assets)
                .Parse(["skills", "install", "--hermes"])
                .InvokeAsync();
            Assert.Equal(0, secondExit);
            var secondOutput = secondStdout.ToString() + secondStderr.ToString();

            Assert.True(_files.HasFile(Path.Combine(hermesHome, "skills", "mohist", "SKILL.md")));
            Assert.Contains("mohist: updated", secondOutput);
            Assert.Contains("mohist-explore: updated", secondOutput);
            Assert.NotEqual(Path.GetFullPath(defaultHermesRoot), Path.GetFullPath(Path.Combine(hermesHome, "skills")));
        }
        finally
        {
            _environment[SkillInstallService.HermesHomeEnvironmentVariable] = null;
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Theory]
    [InlineData("--hermes", "--claude")]
    [InlineData("--hermes", "--path", "repo")]
    public async Task Install_HermesTarget_RejectsIncompatibleOptionsBeforeWriting(params string[] args)
    {
        var hermesHome = Path.Combine("/tmp", $"hermes-home-{Guid.NewGuid():N}");
        var repoPath = Path.Combine("/tmp", $"hermes-repo-{Guid.NewGuid():N}");
        _files.AddDirectory(repoPath);
        _environment[SkillInstallService.HermesHomeEnvironmentVariable] = hermesHome;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            var assets = BuildDefaultAssetService();
            var normalizedArgs = args.Select(arg => arg == "repo" ? repoPath : arg).Prepend("install").Prepend("skills").ToArray();
            var exitCode = await BuildRootCommand(stdout, stderr, assets)
                .Parse(normalizedArgs)
                .InvokeAsync();

            Assert.Equal(1, exitCode);
            Assert.Contains("cannot be combined", stderr.ToString());
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.False(_files.DirectoryExists(Path.Combine(hermesHome, "skills")));
            Assert.False(_files.DirectoryExists(Path.Combine(repoPath, ".agents", "skills")));
        }
        finally
        {
            _environment[SkillInstallService.HermesHomeEnvironmentVariable] = null;
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Install_HermesTarget_DoesNotTouchHermesConfigFiles()
    {
        var hermesHome = Path.Combine("/tmp", $"hermes-config-{Guid.NewGuid():N}");
        var configPath = Path.Combine(hermesHome, "config.yaml");
        _files.AddDirectory(hermesHome);
        _files.AddFile(configPath, "skills:\n  external_dirs:\n    - /existing\n");
        _environment[SkillInstallService.HermesHomeEnvironmentVariable] = hermesHome;

        try
        {
            var assets = BuildDefaultAssetService();
            var exitCode = await BuildRootCommand()
                .Parse(["skills", "install", "--hermes"])
                .InvokeAsync();

            Assert.Equal(0, exitCode);
            Assert.Equal("skills:\n  external_dirs:\n    - /existing\n", _files.ReadAllText(configPath));
        }
        finally
        {
            _environment[SkillInstallService.HermesHomeEnvironmentVariable] = null;
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Install_DoesNotTouchDotMohistSkills()
    {
        _files.SetCurrentDirectory(Path.Combine("/tmp", $"skills-install-runtime-{Guid.NewGuid():N}"));
        var mohistSkillsDir = Path.Combine(_files.Cwd, ".mohist", "skills");
        _files.AddDirectory(mohistSkillsDir);
        var sentinelPath = Path.Combine(mohistSkillsDir, "sentinel.txt");
        _files.AddFile(sentinelPath, "keep");

        var assets = BuildDefaultAssetService();
        var exitCode = await BuildRootCommand()
            .Parse(["skills", "install"])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal("keep", _files.ReadAllText(sentinelPath));
        Assert.False(_files.DirectoryExists(Path.Combine(mohistSkillsDir, "mohist")));
    }

    private SkillAssetService BuildDefaultAssetService()
    {
        var sourceRoot = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "cli", "Mohist.Cli", "skill-data");
        sourceRoot = Path.GetFullPath(sourceRoot);
        var targetRoot = Path.Combine("/tmp", $"skills-install-assets-{Guid.NewGuid():N}");
        _files.AddDirectory(targetRoot);
        _assetRoot = targetRoot;

        if (Directory.Exists(sourceRoot))
        {
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                _files.AddFile(Path.Combine(targetRoot, relative), File.ReadAllText(file));
            }
        }

        var resolver = new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: () => targetRoot,
            getManagedAssetRoot: null,
            getUserHome: () => targetRoot);
        return new SkillAssetService(_files, _environment, resolver);
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
        services.AddSingleton<IServiceInstaller>(sp => new SystemdServiceInstaller(output, error, _files, sp.GetRequiredService<ICommandExecutor>()));
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton(assets ?? BuildDefaultAssetService());
        services.AddSingleton<InfoCollector>();
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

    private void AssertStub(string path, string name)
    {
        Assert.True(_files.HasFile(path), $"Expected skill stub at '{path}'.");
        var content = _files.ReadAllText(path);
        Assert.Contains("---", content);
        Assert.Contains($"name: {name}", content);
        Assert.Contains($"description: {ReadPackagedDescription(name)}", content);
        Assert.Contains($"mo skills get {name}", content);
        Assert.DoesNotContain("<artifact", content);
    }

    private void AssertFullPackagedSkill(string path, string name)
    {
        Assert.True(_files.HasFile(path), $"Expected full Hermes skill at '{path}'.");
        var content = _files.ReadAllText(path);
        Assert.Contains($"name: {name}", content);
        Assert.Contains($"description: {ReadPackagedDescription(name)}", content);
        Assert.Contains("---", content);
        Assert.DoesNotContain("This Mohist-managed discovery stub keeps local agent skill installs lightweight and version-matched.", content);
    }

    private string ReadPackagedDescription(string name)
    {
        var skillFile = Path.Combine(_assetRoot!, name, "SKILL.md");
        var content = _files.ReadAllText(skillFile);
        var line = content.Split('\n').First(l => l.StartsWith("description:", StringComparison.Ordinal));
        return line["description:".Length..].Trim();
    }

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue 或 epic，查看项目状态或日志，或任何涉及 Mohist issue/epic/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。当用户带着一句话、一个模糊念头或未沉淀的改进意图，需要探索当前产品形态和技术实现，最终产出一份用户视角、产品视角、领域视角三段协作的 PRD 时使用。触发词包括 \"提炼需求\"、\"写 PRD\"、\"沉淀 issue\"、\"需求文档\"、\"探索\"、\"完善 issue\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
