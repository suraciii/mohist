using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.Skills;

[Collection("SkillsCli")]
public sealed class SkillsCommandRegistrationSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
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
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(new HttpClient(), TextWriter.Null, TextWriter.Null, RealFileSystem.Instance, new SystemCommandExecutor()));
        services.AddSingleton<TextWriter>(TextWriter.Null);
        services.AddSingleton<IFileSystem>(RealFileSystem.Instance);
        services.AddSingleton<ICommandExecutor>(new SystemCommandExecutor());
        services.AddSingleton<IEnvironmentVariableProvider>(SystemEnvironmentVariableProvider.Instance);
        services.AddSingleton<IServiceInstaller>(sp => new SystemdServiceInstaller(TextWriter.Null, TextWriter.Null, RealFileSystem.Instance, sp.GetRequiredService<ICommandExecutor>()));
        services.AddSingleton(sp => new UpdateOperations(TextWriter.Null, TextWriter.Null, sp.GetRequiredService<IServiceInstaller>(), sp.GetRequiredService<ICommandExecutor>(), RealFileSystem.Instance, sp.GetRequiredService<IEnvironmentVariableProvider>()));
        services.AddSingleton(new RuntimeConsistencyValidator(new HttpClient(), new SystemCommandExecutor(), RealFileSystem.Instance, SystemEnvironmentVariableProvider.Instance, TextWriter.Null));
        services.AddSingleton(new ServiceReadinessProbe(new HttpClient(), TextWriter.Null));
        services.AddSingleton(new RunnerRefreshVerifier(new HttpClient(), new SystemCommandExecutor(), RealFileSystem.Instance));
        services.AddSingleton(new UpdateOutcomeReporter(new HttpClient(), TextWriter.Null));
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton<SkillAssetService>();
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
