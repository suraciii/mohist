using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Specs.Skills;

[Collection("SkillsCli")]
public sealed class SkillsCliRuntimeSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task SkillsCommands_DoesNotThrowDependencyInjectionFailures_WhenListingHelp()
    {
        var files = new FakeFileSystem();
        var environment = new MockEnvironmentVariableProvider();
        var (exitCode, _, stderr) = await InvokeSkillsAsync(files, environment, "skills", "--help");

        Assert.True(exitCode == 0, $"exit={exitCode} stderr:\n{stderr}");
        Assert.DoesNotContain("Unable to resolve service for type 'System.IO.TextWriter'", stderr);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task CliProgram_Main_CanExecuteReadOnlySkillsCommand_ThroughRealCompositionPath()
    {
        var files = new FakeFileSystem();
        var environment = new MockEnvironmentVariableProvider();
        var overrideRoot = Path.Combine("/tmp", $"mohist-cli-runtime-{Guid.NewGuid():N}");
        files.AddDirectory(overrideRoot);
        files.AddFile(
            Path.Combine(overrideRoot, "mohist", "SKILL.md"),
            $"---\nname: mohist\ndescription: {DescriptionFor("mohist")}\n---\n\n# body\n");
        files.AddFile(
            Path.Combine(overrideRoot, "mohist-explore", "SKILL.md"),
            $"---\nname: mohist-explore\ndescription: {DescriptionFor("mohist-explore")}\n---\n\n# explore\n");
        SkillAssetManifest.Write(
            overrideRoot,
            SkillAssetManifest.ResolveCurrentBuildIdentity(),
            new[] { "mohist", "mohist-explore" },
            files);
        environment[SkillAssetRootResolver.OverrideEnvironmentVariable] = overrideRoot;

        var (exitCode, stdout, stderr) = await InvokeSkillsAsync(files, environment, "skills", "get", "mohist");

        Assert.True(exitCode == 0, $"exit={exitCode} stdout:\n{stdout}\n\nstderr:\n{stderr}");
        Assert.Contains("name: mohist", stdout);
        Assert.DoesNotContain("mo skills get mohist --full", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void CliProgram_PublishableConfiguration_IncludesPackagedSkillData()
    {
        var csprojPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "cli", "Mohist.Cli", "Mohist.Cli.csproj"));
        var csprojContent = File.ReadAllText(csprojPath);

        Assert.Contains("skill-data\\**\\*", csprojContent, StringComparison.Ordinal);
        Assert.Contains("PackagePath=\"skill-data/", csprojContent, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeSkillsAsync(
        FakeFileSystem files,
        MockEnvironmentVariableProvider environment,
        params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient
            {
                BaseAddress = new Uri(environment.GetEnvironmentVariable("MOHIST_SERVER_URL") ?? "http://localhost:3456"),
                Timeout = TimeSpan.FromSeconds(30),
            },
            args,
            stdout,
            stderr,
            files,
            new SystemCommandExecutor(),
            environment);

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "从产品和用户视角探索 mohist 项目，发现功能缺陷、体验问题、设计机会和价值增长点。当用户想要探索代码库、发现改进点、审查用户体验、思考功能设计、或无目标地巡检产品时使用。触发词包括 \"explore\"、\"探索\"、\"巡检\"、\"找问题\"、\"体验审查\"、\"功能设计\"、\"产品思考\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
