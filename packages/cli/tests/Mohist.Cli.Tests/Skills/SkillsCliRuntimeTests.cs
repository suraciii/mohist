using Mohist.Cli.Tests.Compatibility;
using Mohist.Cli;
using CliCompositionTestFactory = Mohist.Cli.Tests.Support.CliCompositionTestFactory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
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
        var (exitCode, _, stderr) = await InvokeSkillsAsync(files, environment, null, "skill", "--help");

        Assert.True(exitCode == 0, $"exit={exitCode} stderr:\n{stderr}");
        Assert.DoesNotContain("Unable to resolve service for type 'System.IO.TextWriter'", stderr);
    }

    [Fact]
    public async Task CliProgram_Main_CanExecuteReadOnlySkillsCommand_ThroughRealCompositionPath()
    {
        var files = new FakeFileSystem();
        var environment = new MockEnvironmentVariableProvider();
        var overrideRoot = "/mohist-tests/cli-runtime/assets";
        files.AddDirectory(overrideRoot);
        files.AddFile(
            Path.Combine(overrideRoot, "mohist", "SKILL.md"),
            $"---\nname: mohist\ndescription: {DescriptionFor("mohist")}\n---\n\n# body\n");
        files.AddFile(
            Path.Combine(overrideRoot, "mohist-explore", "SKILL.md"),
            $"---\nname: mohist-explore\ndescription: {DescriptionFor("mohist-explore")}\n---\n\n# explore\n");
        environment[SkillAssetRootResolver.OverrideEnvironmentVariable] = overrideRoot;
        var (exitCode, stdout, stderr) = await InvokeThroughProgramAdapterAsync(
            files,
            environment,
            "skill", "view", "mohist");

        Assert.True(exitCode == 0, $"exit={exitCode} stdout:\n{stdout}\n\nstderr:\n{stderr}");
        Assert.Contains("name: mohist", stdout);
        Assert.DoesNotContain("mo skills get mohist --full", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public async Task TestComposition_InjectsDeterministicAmbientCapabilitiesThroughTheGraph()
    {
        var files = new FakeFileSystem();
        var environment = new MockEnvironmentVariableProvider();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var assets = CreateHelpOnlyAssets(files, environment);
        var commands = new NoopCommandExecutor();
        using var http = RejectingHttpMessageHandler.CreateClient();
        var composition = CliCompositionTestFactory.Create(
            files,
            environment,
            assets,
            output,
            error,
            http,
            commands);

        Assert.IsType<FakeTimeProvider>(composition.Api.TimeProvider);
        Assert.Same(files, composition.Api.FileSystem);
        Assert.Same(files, composition.Services.GetRequiredService<IFileSystem>());
        Assert.Same(environment, composition.Services.GetRequiredService<IEnvironmentVariableProvider>());
        Assert.Same(commands, composition.Api.CommandExecutor);
        Assert.Same(commands, composition.Services.GetRequiredService<ICommandExecutor>());
        Assert.Same(http, composition.Api.Http);
        Assert.Same(http, composition.Services.GetRequiredService<HttpClient>());
        Assert.Same(assets, composition.Services.GetRequiredService<SkillAssetService>());
        Assert.IsType<Mohist.Cli.Tests.Support.FakeServiceInstaller>(
            composition.Services.GetRequiredService<IServiceInstaller>());
        Assert.IsType<Mohist.Cli.Tests.Support.FakeSourceCodeUpdater>(
            composition.Services.GetRequiredService<SourceCodeUpdater>());
        Assert.Equal("/mohist-tests/user", composition.Api.GetUserHome());
        Assert.Same(TextReader.Null, composition.Api.StandardInput);
        Assert.Same(
            composition.Api.TimeProvider,
            composition.Services.GetRequiredService<RuntimeConsistencyValidator>().TimeProvider);
        Assert.Same(
            composition.Api.TimeProvider,
            composition.Services.GetRequiredService<ServiceReadinessProbe>().TimeProvider);
        Assert.Same(
            composition.Api.PollWait,
            composition.Services.GetRequiredService<ServiceReadinessProbe>().PollWait);
        Assert.Same(
            composition.Api.PollWait,
            composition.Services.GetRequiredService<RuntimeConsistencyValidator>().PollWait);
        Assert.Same(
            composition.Api.TimeProvider,
            composition.Services.GetRequiredService<RunnerRefreshVerifier>().TimeProvider);
        Assert.Same(
            composition.Api.PollWait,
            composition.Services.GetRequiredService<RunnerRefreshVerifier>().PollWait);
        Assert.Equal(
            "mohist-test-host",
            composition.Services.GetRequiredService<RunnerRefreshVerifier>().LocalHostname);
        await composition.Api.PollWait(TimeSpan.FromDays(1), CancellationToken.None);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeSkillsAsync(
        FakeFileSystem files,
        MockEnvironmentVariableProvider environment,
        SkillAssetService? assets,
        params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        assets ??= CreateHelpOnlyAssets(files, environment);
        var composition = CliCompositionTestFactory.Create(files, environment, assets, stdout, stderr);

        var exitCode = await composition.Root.Parse(args).InvokeAsync(new System.CommandLine.InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
        });

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeThroughProgramAdapterAsync(
        FakeFileSystem files,
        MockEnvironmentVariableProvider environment,
        params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var exitCode = await MohistCliCommands.RunAsync(
            RejectingHttpMessageHandler.CreateClient(),
            args,
            stdout,
            stderr,
            files,
            new NoopCommandExecutor(),
            environment,
            standardInput: TextReader.Null,
            installer: new Mohist.Cli.Tests.Support.FakeServiceInstaller(),
            updater: new Mohist.Cli.Tests.Support.FakeSourceCodeUpdater(),
            getUserHome: () => "/mohist-tests/user",
            timeProvider: timeProvider,
            pollWait: (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                timeProvider.Advance(delay);
                return Task.CompletedTask;
            },
            getLocalHostname: () => "mohist-test-host");
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static SkillAssetService CreateHelpOnlyAssets(
        FakeFileSystem files,
        MockEnvironmentVariableProvider environment)
    {
        var root = "/mohist-tests/cli-runtime/help-assets";
        files.AddDirectory(root);
        var resolver = new SkillAssetRootResolver(
            files,
            environment,
            getOverrideAssetRoot: () => root,
            getManagedAssetRoot: null,
            getUserHome: () => "/mohist-tests/user");
        return new SkillAssetService(files, environment, resolver);
    }

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue 或 epic，查看项目状态或日志，或任何涉及 Mohist issue/epic/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。当用户带着一句话、一个模糊念头或未沉淀的改进意图，需要探索当前产品形态和技术实现，最终产出一份用户视角、产品视角、领域视角三段协作的 PRD 时使用。触发词包括 \"提炼需求\"、\"写 PRD\"、\"沉淀 issue\"、\"需求文档\"、\"探索\"、\"完善 issue\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
