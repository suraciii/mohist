using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("SkillsCli")]
public sealed class SkillsInstallSpecs : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mohist-skills-install-{Guid.NewGuid():N}");
    private readonly string _originalDirectory;

    public SkillsInstallSpecs()
    {
        _originalDirectory = TryGetCurrentDirectory();
        Environment.SetEnvironmentVariable("HERMES_HOME", null);
    }

    [Fact]
    public async Task Install_DefaultTarget_WritesBuiltInDiscoveryStubsUnderAgentsSkills()
    {
        Directory.CreateDirectory(_tempRoot);
        try
        {
            Directory.SetCurrentDirectory(_tempRoot);
            var exitCode = await BuildRootCommand().Parse(["skills", "install"]).InvokeAsync();

            Assert.Equal(0, exitCode);
            AssertStub(Path.Combine(_tempRoot, ".agents", "skills", "mohist", "SKILL.md"), "mohist");
            AssertStub(Path.Combine(_tempRoot, ".agents", "skills", "mohist-explore", "SKILL.md"), "mohist-explore");
        }
        finally
        {
            Directory.SetCurrentDirectory(_originalDirectory);
        }
    }

    [Fact]
    public async Task Install_PathTarget_WritesOnlyToSelectedRepository()
    {
        var currentRoot = Path.Combine(_tempRoot, "current");
        var targetRoot = Path.Combine(_tempRoot, "target");
        Directory.CreateDirectory(currentRoot);
        Directory.CreateDirectory(targetRoot);
        try
        {
            Directory.SetCurrentDirectory(currentRoot);
            var exitCode = await BuildRootCommand().Parse(["skills", "install", "--path", targetRoot]).InvokeAsync();

            Assert.Equal(0, exitCode);
            AssertStub(Path.Combine(targetRoot, ".agents", "skills", "mohist", "SKILL.md"), "mohist");
            Assert.False(File.Exists(Path.Combine(currentRoot, ".agents", "skills", "mohist", "SKILL.md")));
        }
        finally
        {
            Directory.SetCurrentDirectory(_originalDirectory);
        }
    }

    [Fact]
    public async Task Install_ClaudeTarget_WritesClaudeSkillsOnly()
    {
        Directory.CreateDirectory(_tempRoot);
        try
        {
            Directory.SetCurrentDirectory(_tempRoot);
            var exitCode = await BuildRootCommand().Parse(["skills", "install", "--claude"]).InvokeAsync();

            Assert.Equal(0, exitCode);
            AssertStub(Path.Combine(_tempRoot, ".claude", "skills", "mohist", "SKILL.md"), "mohist");
            AssertStub(Path.Combine(_tempRoot, ".claude", "skills", "mohist-explore", "SKILL.md"), "mohist-explore");
            Assert.False(File.Exists(Path.Combine(_tempRoot, ".agents", "skills", "mohist", "SKILL.md")));
            Assert.False(File.Exists(Path.Combine(_tempRoot, ".agents", "skills", "mohist-explore", "SKILL.md")));
        }
        finally
        {
            Directory.SetCurrentDirectory(_originalDirectory);
        }
    }

    [Fact]
    public async Task Install_OverwritesExistingBuiltInStubContent()
    {
        var skillDir = Path.Combine(_tempRoot, ".agents", "skills", "mohist");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), "old content");
        try
        {
            Directory.SetCurrentDirectory(_tempRoot);
            var exitCode = await BuildRootCommand().Parse(["skills", "install"]).InvokeAsync();

            Assert.Equal(0, exitCode);
            AssertStub(Path.Combine(skillDir, "SKILL.md"), "mohist");
            Assert.DoesNotContain("old content", await File.ReadAllTextAsync(Path.Combine(skillDir, "SKILL.md")));
        }
        finally
        {
            Directory.SetCurrentDirectory(_originalDirectory);
        }
    }

    [Fact]
    public async Task Install_DoesNotTouchUnrelatedUserAuthoredSkillDirectories()
    {
        var userSkillDir = Path.Combine(_tempRoot, ".agents", "skills", "mohist-po");
        Directory.CreateDirectory(userSkillDir);
        var sentinelPath = Path.Combine(userSkillDir, "SKILL.md");
        await File.WriteAllTextAsync(sentinelPath, "user-authored");
        try
        {
            Directory.SetCurrentDirectory(_tempRoot);
            var exitCode = await BuildRootCommand().Parse(["skills", "install"]).InvokeAsync();

            Assert.Equal(0, exitCode);
            Assert.Equal("user-authored", await File.ReadAllTextAsync(sentinelPath));
            Assert.False(Directory.Exists(Path.Combine(_tempRoot, ".claude", "skills", "mohist-po")));
        }
        finally
        {
            Directory.SetCurrentDirectory(_originalDirectory);
        }
    }

    [Fact]
    public async Task Install_HermesTarget_CopiesFullPackagedSkillData()
    {
        var hermesHome = Path.Combine(_tempRoot, "hermes-home");
        Environment.SetEnvironmentVariable("HERMES_HOME", hermesHome);
        Directory.CreateDirectory(_tempRoot);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Directory.SetCurrentDirectory(_tempRoot);
            var exitCode = await BuildRootCommand(stdout, stderr).Parse(["skills", "install", "--hermes"]).InvokeAsync();

            Assert.Equal(0, exitCode);
            var mohistSkill = Path.Combine(hermesHome, "skills", "mohist", "SKILL.md");
            var exploreSkill = Path.Combine(hermesHome, "skills", "mohist-explore", "SKILL.md");
            AssertFullPackagedSkill(mohistSkill, "mohist");
            AssertFullPackagedSkill(exploreSkill, "mohist-explore");
            Assert.True(File.Exists(Path.Combine(hermesHome, "skills", "mohist", "references", "issue-templates.md")));
            Assert.False(Directory.Exists(Path.Combine(hermesHome, "skills", "mohist-po")));
            Assert.False(Directory.Exists(Path.Combine(_tempRoot, ".agents", "skills")));
            Assert.False(Directory.Exists(Path.Combine(_tempRoot, ".claude", "skills")));

            var output = stdout.ToString();
            Assert.Contains("mohist: created", output);
            Assert.Contains("mohist-explore: created", output);
            Assert.Contains("/mohist", output);
            Assert.Contains("/mohist-explore", output);
            Assert.Contains("reload/reset", output);
        }
        finally
        {
            RestoreCurrentDirectory();
            Environment.SetEnvironmentVariable("HERMES_HOME", null);
        }
    }

    [Fact]
    public async Task Install_HermesTarget_UsesConfiguredHermesHomeAndReportsUpdatedOnRepeatInstall()
    {
        var hermesHome = Path.Combine(_tempRoot, "custom-hermes-home");
        var defaultHermesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes", "skills");
        Environment.SetEnvironmentVariable("HERMES_HOME", hermesHome);
        Directory.CreateDirectory(_tempRoot);

        try
        {
            var (firstExitCode, _, _) = await InvokeCliProcessAsync("skills install --hermes", _tempRoot, ("HERMES_HOME", hermesHome));
            var (secondExitCode, secondStdout, secondStderr) = await InvokeCliProcessAsync("skills install --hermes", _tempRoot, ("HERMES_HOME", hermesHome));
            var secondOutput = secondStdout + secondStderr;

            Assert.Equal(0, firstExitCode);
            Assert.Equal(0, secondExitCode);
            Assert.True(File.Exists(Path.Combine(hermesHome, "skills", "mohist", "SKILL.md")));
            Assert.Contains("mohist: updated", secondOutput);
            Assert.Contains("mohist-explore: updated", secondOutput);
            Assert.NotEqual(Path.GetFullPath(defaultHermesRoot), Path.GetFullPath(Path.Combine(hermesHome, "skills")));
        }
        finally
        {
            RestoreCurrentDirectory();
            Environment.SetEnvironmentVariable("HERMES_HOME", null);
        }
    }

    [Theory]
    [InlineData("--hermes", "--claude")]
    [InlineData("--hermes", "--path", "repo")]
    public async Task Install_HermesTarget_RejectsIncompatibleOptionsBeforeWriting(params string[] args)
    {
        var hermesHome = Path.Combine(_tempRoot, "hermes-home");
        var repoPath = Path.Combine(_tempRoot, "repo");
        Environment.SetEnvironmentVariable("HERMES_HOME", hermesHome);
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(repoPath);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Directory.SetCurrentDirectory(_tempRoot);
            var normalizedArgs = args.Select(arg => arg == "repo" ? repoPath : arg).Prepend("install").Prepend("skills").ToArray();
            var exitCode = await BuildRootCommand(stdout, stderr).Parse(normalizedArgs).InvokeAsync();

            Assert.Equal(1, exitCode);
            Assert.Contains("cannot be combined", stderr.ToString());
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.False(Directory.Exists(Path.Combine(hermesHome, "skills")));
            Assert.False(Directory.Exists(Path.Combine(_tempRoot, ".claude", "skills")));
            Assert.False(Directory.Exists(Path.Combine(repoPath, ".agents", "skills")));
        }
        finally
        {
            Directory.SetCurrentDirectory(_originalDirectory);
            Environment.SetEnvironmentVariable("HERMES_HOME", null);
        }
    }

    [Fact]
    public async Task Install_HermesTarget_DoesNotTouchHermesConfigFiles()
    {
        var hermesHome = Path.Combine(_tempRoot, "hermes-home");
        var configPath = Path.Combine(hermesHome, "config.yaml");
        Environment.SetEnvironmentVariable("HERMES_HOME", hermesHome);
        Directory.CreateDirectory(hermesHome);
        await File.WriteAllTextAsync(configPath, "skills:\n  external_dirs:\n    - /existing\n");
        try
        {
            Directory.SetCurrentDirectory(_tempRoot);
            var exitCode = await BuildRootCommand().Parse(["skills", "install", "--hermes"]).InvokeAsync();

            Assert.Equal(0, exitCode);
            Assert.Equal("skills:\n  external_dirs:\n    - /existing\n", await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            Directory.SetCurrentDirectory(_originalDirectory);
            Environment.SetEnvironmentVariable("HERMES_HOME", null);
        }
    }

    [Fact]
    public async Task Install_DoesNotTouchDotMohistSkills()
    {
        var mohistSkillsDir = Path.Combine(_tempRoot, ".mohist", "skills");
        Directory.CreateDirectory(mohistSkillsDir);
        var sentinelPath = Path.Combine(mohistSkillsDir, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "keep");
        try
        {
            Directory.SetCurrentDirectory(_tempRoot);
            var exitCode = await BuildRootCommand().Parse(["skills", "install"]).InvokeAsync();

            Assert.Equal(0, exitCode);
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinelPath));
            Assert.False(Directory.Exists(Path.Combine(mohistSkillsDir, "mohist")));
        }
        finally
        {
            Directory.SetCurrentDirectory(_originalDirectory);
        }
    }

    public void Dispose()
    {
        RestoreCurrentDirectory();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private void RestoreCurrentDirectory()
    {
        var target = Directory.Exists(_originalDirectory) ? _originalDirectory : Path.GetTempPath();
        if (Directory.Exists(target))
            Directory.SetCurrentDirectory(target);
    }

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

    private static System.CommandLine.RootCommand BuildRootCommand(TextWriter? output = null, TextWriter? error = null)
    {
        output ??= TextWriter.Null;
        error ??= TextWriter.Null;
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(new HttpClient(), output, error, RealFileSystem.Instance, new SystemCommandExecutor()));
        services.AddSingleton<TextWriter>(output);
        services.AddSingleton<SkillInstallService>(_ => new SkillInstallService(
            _.GetRequiredService<SkillAssetService>(),
            _.GetRequiredService<IFileSystem>(),
            output,
            error));
        services.AddSingleton<IFileSystem>(RealFileSystem.Instance);
        services.AddSingleton<ICommandExecutor>(new SystemCommandExecutor());
        services.AddSingleton<SystemdServiceInstaller>();
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton<SkillAssetService>();

        var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<MohistCliApi>();
        return MohistCliCommands.Build(api, provider);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeCliMainAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();

        try
        {
            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);
            var exitCode = await CliProgram.Main(args);
            return (exitCode, stdoutWriter.ToString(), stderrWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeCliProcessAsync(string arguments, string workingDirectory, params (string Name, string Value)[] environment)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../"));
        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet", $"run --project packages/cli/Mohist.Cli -- {arguments}")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["DOTNET_CLI_WORKING_DIR"] = workingDirectory;
        foreach (var (name, value) in environment)
            startInfo.Environment[name] = value;

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    private static void AssertStub(string path, string name)
    {
        Assert.True(File.Exists(path), $"Expected skill stub at '{path}'.");
        var content = File.ReadAllText(path);
        Assert.Contains("---", content);
        Assert.Contains($"name: {name}", content);
        Assert.Contains($"description: {DescriptionFor(name)}", content);
        Assert.Contains($"mo skills get {name}", content);
        Assert.DoesNotContain("<artifact", content);
    }

    private static void AssertFullPackagedSkill(string path, string name)
    {
        Assert.True(File.Exists(path), $"Expected full Hermes skill at '{path}'.");
        var content = File.ReadAllText(path);
        Assert.Contains($"name: {name}", content);
        Assert.Contains($"description: {DescriptionFor(name)}", content);
        Assert.Contains("---", content);
        Assert.DoesNotContain("This Mohist-managed discovery stub keeps local agent skill installs lightweight and version-matched.", content);
    }

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "从产品和用户视角探索 mohist 项目，发现功能缺陷、体验问题、设计机会和价值增长点。当用户想要探索代码库、发现改进点、审查用户体验、思考功能设计、或无目标地巡检产品时使用。触发词包括 \"explore\"、\"探索\"、\"巡检\"、\"找问题\"、\"体验审查\"、\"功能设计\"、\"产品思考\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
