using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Cli.Tests.Compatibility;
using Mohist.Cli.Tests.Support;
using System.CommandLine;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CommandPresentationCoverageValidatorSpecs
{
    [Fact]
    public void RealCommandTree_HasExplicitCoverageForEveryVisibleNode()
    {
        var files = new Mohist.Cli.Tests.Support.FakeFileSystem();
        var executor = new FakeCommandExecutor();
        var root = BuildRealRoot(files, executor);

        var diagnostics = CommandPresentationCoverageValidator.Validate(root);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MissingRootPresentationAndClassification_ReportsCompletePath()
    {
        var root = new RootCommand();
        root.Subcommands.Add(new Command("catalog", "registration description"));

        var diagnostics = CommandPresentationCoverageValidator.Validate(root);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("mo catalog", diagnostic.InvocationPath);
        Assert.Contains("mo catalog", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("presentation", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("capability classification", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRootClassification_ReportsClassificationWithoutUsingDescription()
    {
        var root = new RootCommand();
        var command = new Command("catalog", "registration description");
        root.Subcommands.Add(command);
        CommandPresentationCatalog.Attach(command, new CommandPresentation(null, "Catalog commands"));

        var diagnostics = CommandPresentationCoverageValidator.Validate(root);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("mo catalog", diagnostic.InvocationPath);
        Assert.DoesNotContain("presentation", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("capability classification", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDescendants_ReportsAllPathsAtArbitraryDepthInStableOrder()
    {
        var root = new RootCommand();
        var covered = new Command("covered", "registration description");
        var group = new Command("group", "registration description");
        var deep = new Command("deep", "registration description");
        var missingLeaf = new Command("leaf", "registration description");
        var missingGroup = new Command("missing", "registration description");
        var missingChild = new Command("child", "registration description");
        root.Subcommands.Add(group);
        root.Subcommands.Add(covered);
        group.Subcommands.Add(deep);
        group.Subcommands.Add(missingGroup);
        deep.Subcommands.Add(missingLeaf);
        missingGroup.Subcommands.Add(missingChild);
        CommandPresentationCatalog.Attach(group, new CommandPresentation(CommandCapability.Automation, "A group"));
        CommandPresentationCatalog.Attach(deep, new CommandPresentation(CommandCapability.Automation, "A deep group"));
        CommandPresentationCatalog.Attach(covered, new CommandPresentation(CommandCapability.Work, "A covered command"));

        var diagnostics = CommandPresentationCoverageValidator.Validate(root);

        Assert.Equal(
            [
                "mo group deep leaf",
                "mo group missing",
                "mo group missing child",
            ],
            diagnostics.Select(diagnostic => diagnostic.InvocationPath));
        Assert.All(diagnostics, diagnostic =>
            Assert.Contains(diagnostic.InvocationPath, diagnostic.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyOrWhitespaceSummary_IsNotCoverage()
    {
        var root = new RootCommand();
        var rootCommand = new Command("root-area", "registration description");
        var descendant = new Command("descendant", "registration description");
        rootCommand.Subcommands.Add(descendant);
        root.Subcommands.Add(rootCommand);
        CommandPresentationCatalog.Attach(rootCommand, new CommandPresentation(CommandCapability.Work, "   "));
        CommandPresentationCatalog.Attach(descendant, new CommandPresentation(CommandCapability.Work, "\t"));

        var diagnostics = CommandPresentationCoverageValidator.Validate(root);

        Assert.Equal(
            ["mo root-area", "mo root-area descendant"],
            diagnostics.Select(diagnostic => diagnostic.InvocationPath));
    }

    [Fact]
    public void HiddenNodesAreExcludedButVisibleDescendantsAreStillTraversed()
    {
        var root = new RootCommand();
        var hidden = new Command("hidden", "hidden registration description") { Hidden = true };
        var hiddenChild = new Command("hidden-child", "hidden descendant registration description");
        var visible = new Command("visible", "visible registration description");
        hidden.Subcommands.Add(hiddenChild);
        root.Subcommands.Add(hidden);
        root.Subcommands.Add(visible);
        CommandPresentationCatalog.Attach(visible, new CommandPresentation(CommandCapability.Tools, "Visible command"));

        var diagnostics = CommandPresentationCoverageValidator.Validate(root);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("mo hidden hidden-child", diagnostic.InvocationPath);
    }

    [Fact]
    public void DiagnosticsAreOrderedByCompleteInvocationPath()
    {
        var root = new RootCommand();
        var zulu = new Command("zulu");
        var alpha = new Command("alpha");
        root.Subcommands.Add(zulu);
        root.Subcommands.Add(alpha);

        var diagnostics = CommandPresentationCoverageValidator.Validate(root);

        Assert.Equal(["mo alpha", "mo zulu"], diagnostics.Select(diagnostic => diagnostic.InvocationPath));
    }

    private static RootCommand BuildRealRoot(Mohist.Cli.Tests.Support.FakeFileSystem files, FakeCommandExecutor executor)
    {
        var environment = new MockEnvironmentVariableProvider();
        var http = RejectingHttpMessageHandler.CreateClient();
        var api = new MohistCliApi(http, TextWriter.Null, TextWriter.Null, files, executor);
        var installer = new FakeServiceInstaller();
        var assets = new SkillAssetService(files, environment);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            TextWriter.Null,
            TextWriter.Null,
            installer,
            executor,
            files,
            environment,
            http,
            timeProvider: new FakeTimeProvider(),
            managedUpdatesEnabled: false);

        var services = new ServiceCollection();
        services.AddSingleton(api);
        services.AddSingleton<IFileSystem>(files);
        services.AddSingleton<ICommandExecutor>(executor);
        services.AddSingleton<IEnvironmentVariableProvider>(environment);
        services.AddSingleton<IServiceInstaller>(installer);
        services.AddSingleton(updater);
        services.AddSingleton(assets);
        services.AddSingleton(new SkillInstallService(assets, files, environment, TextWriter.Null, TextWriter.Null));

        using var provider = services.BuildServiceProvider();
        return MohistCliCommands.Build(api, provider);
    }
}
