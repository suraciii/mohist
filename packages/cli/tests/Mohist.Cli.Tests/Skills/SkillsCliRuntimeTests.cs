using Mohist.Cli.Tests.Compatibility;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Cli.Tests.Skills;

public sealed class SkillsCliRuntimeTests
{
    [Fact]
    public async Task SkillsCommands_DoesNotThrowDependencyInjectionFailures_WhenListingHelp()
    {
        var files = new FakeFileSystem();
        var environment = new MockEnvironmentVariableProvider();
        var (exitCode, _, stderr) = await InvokeSkillsAsync(files, environment, "skill", "--help");

        Assert.True(exitCode == 0, $"exit={exitCode} stderr:\n{stderr}");
        Assert.DoesNotContain("Unable to resolve service for type 'System.IO.TextWriter'", stderr);
    }

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
        environment[SkillAssetRootResolver.OverrideEnvironmentVariable] = overrideRoot;

        var (exitCode, stdout, stderr) = await InvokeSkillsAsync(files, environment, "skill", "view", "mohist");

        Assert.True(exitCode == 0, $"exit={exitCode} stdout:\n{stdout}\n\nstderr:\n{stderr}");
        Assert.Contains("name: mohist", stdout);
        Assert.DoesNotContain("mo skills get mohist --full", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeSkillsAsync(
        FakeFileSystem files,
        MockEnvironmentVariableProvider environment,
        params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            RejectingHttpMessageHandler.CreateClient(),
            args,
            stdout,
            stderr,
            files,
            new NoopCommandExecutor(),
            environment);

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue 或 epic，查看项目状态或日志，或任何涉及 Mohist issue/epic/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。当用户带着一句话、一个模糊念头或未沉淀的改进意图，需要探索当前产品形态和技术实现，最终产出一份用户视角、产品视角、领域视角三段协作的 PRD 时使用。触发词包括 \"提炼需求\"、\"写 PRD\"、\"沉淀 issue\"、\"需求文档\"、\"探索\"、\"完善 issue\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
