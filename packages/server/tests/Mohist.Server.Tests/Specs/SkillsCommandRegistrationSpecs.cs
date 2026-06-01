using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("SkillsCli")]
public sealed class SkillsCommandRegistrationSpecs
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

    private static System.CommandLine.RootCommand BuildRootCommand()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(new HttpClient(), TextWriter.Null, TextWriter.Null, RealFileSystem.Instance, new SystemCommandExecutor()));
        services.AddSingleton<TextWriter>(TextWriter.Null);
        services.AddSingleton<IFileSystem>(RealFileSystem.Instance);
        services.AddSingleton<ICommandExecutor>(new SystemCommandExecutor());
        services.AddSingleton<SystemdServiceInstaller>();
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton<SkillAssetService>();
        services.AddSingleton<SkillInstallService>();

        var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<MohistCliApi>();
        return MohistCliCommands.Build(api, provider);
    }

    private static string RenderHelp(System.CommandLine.RootCommand root, string[] args)
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
