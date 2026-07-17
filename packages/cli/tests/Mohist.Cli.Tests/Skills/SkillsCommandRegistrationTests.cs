using Mohist.Cli.Tests.Compatibility;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using EnvironmentAbstractions.TestHelpers;
using Xunit;

namespace Mohist.Cli.Tests.Skills;

[Collection("ConsoleCapture")]
public sealed class SkillsCommandRegistrationTests
{
    [Fact]
    public void SkillsHelp_DescribesCoderAgentSkillManagement_AndListsExpectedSubcommands()
    {
        var root = BuildRootCommand();
        var help = RenderHelp(root, ["skills", "--help"]);

        Assert.Contains("Manage coder agent skills", help);
        Assert.Contains("install", help);
        Assert.Contains("list", help);
        Assert.Contains("get", help);
        Assert.Contains("path", help);
        Assert.DoesNotContain("update", help);
    }

    [Fact]
    public void SkillsUpdate_IsNotRegistered()
    {
        var root = BuildRootCommand();
        var parseResult = root.Parse(["skills", "update"]);

        Assert.NotEmpty(parseResult.Errors);
        Assert.Contains(parseResult.Errors, error => error.Message.Contains("update", StringComparison.Ordinal));
    }

    private static global::System.CommandLine.RootCommand BuildRootCommand()
    {
        var files = new FakeFileSystem();
        var environment =
            new MockEnvironmentVariableProvider();
        EmbeddedSkillData.Populate(files);
        var resolver = new SkillAssetRootResolver(
            files,
            environment,
            getOverrideAssetRoot:
                () => EmbeddedSkillData.VirtualRoot,
            getManagedAssetRoot: null,
            getUserHome:
                () => "/mohist-tests/user");
        var assets = new SkillAssetService(
            files,
            environment,
            resolver);
        var commands = new NoopCommandExecutor();
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            TextWriter.Null,
            TextWriter.Null,
            files,
            commands));
        services.AddSingleton<TextWriter>(TextWriter.Null);
        services.AddSingleton<IFileSystem>(files);
        services.AddSingleton<ICommandExecutor>(commands);
        services.AddSingleton<IEnvironmentVariableProvider>(
            environment);
        services.AddSingleton<IServiceInstaller>(sp =>
            new SystemdServiceInstaller(
                TextWriter.Null,
                TextWriter.Null,
                files,
                sp.GetRequiredService<ICommandExecutor>()));
        services.AddSingleton(sp => new UpdateOperations(
            TextWriter.Null,
            TextWriter.Null,
            sp.GetRequiredService<IServiceInstaller>(),
            sp.GetRequiredService<ICommandExecutor>(),
            files,
            environment));
        services.AddSingleton(new RuntimeConsistencyValidator(
            RejectingHttpMessageHandler.CreateClient(),
            commands,
            files,
            environment,
            TextWriter.Null));
        services.AddSingleton(new ServiceReadinessProbe(
            RejectingHttpMessageHandler.CreateClient(),
            TextWriter.Null));
        services.AddSingleton(new RunnerRefreshVerifier(
            RejectingHttpMessageHandler.CreateClient(),
            commands,
            files));
        services.AddSingleton(new UpdateOutcomeReporter(
            RejectingHttpMessageHandler.CreateClient(),
            TextWriter.Null));
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton(assets);
        services.AddSingleton<SkillInstallService>();
        services.AddSingleton<InfoVerboseCollector>();
        services.AddSingleton<InfoCollector>();
        services.AddSingleton<InfoRenderer>();

        var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<MohistCliApi>();
        return MohistCliCommands.Build(api, provider);
    }

    private static string RenderHelp(global::System.CommandLine.RootCommand root, string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            root.Parse(args).Invoke();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
