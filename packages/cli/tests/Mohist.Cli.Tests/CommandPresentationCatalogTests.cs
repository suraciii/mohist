using System.CommandLine;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CommandPresentationCatalogTests
{
    [Fact]
    public void MetadataIsOwnedByEachRootAndOption()
    {
        var firstRoot = new CliRootCommand("first");
        var firstGroup = new Command("group");
        var firstCommand = new Command("same");
        firstGroup.Subcommands.Add(firstCommand);
        firstRoot.Subcommands.Add(firstGroup);
        var secondRoot = new CliRootCommand("second");
        var secondGroup = new Command("group");
        var secondCommand = new Command("same");
        secondGroup.Subcommands.Add(secondCommand);
        secondRoot.Subcommands.Add(secondGroup);

        var firstPresentation = new CommandPresentation(CommandCapability.Work, "first");
        var secondPresentation = new CommandPresentation(CommandCapability.Tools, "second");
        CommandPresentationCatalog.Attach(firstCommand, firstPresentation);
        CommandPresentationCatalog.Attach(secondCommand, secondPresentation);

        var firstOption = new CliJsonOption("--json");
        var secondOption = new CliJsonOption("--json");
        CommandPresentationCatalog.AttachJsonFields(
            firstOption,
            new ResourceDescriptor(ResourceCardinality.Single, ["first"]));
        CommandPresentationCatalog.AttachJsonFields(
            secondOption,
            new ResourceDescriptor(ResourceCardinality.Single, ["second"]));

        Assert.Same(firstPresentation, CommandPresentationCatalog.Get(firstCommand));
        Assert.Same(secondPresentation, CommandPresentationCatalog.Get(secondCommand));
        Assert.Equal(["first"], CommandPresentationCatalog.GetJsonFields(firstOption));
        Assert.Equal(["second"], CommandPresentationCatalog.GetJsonFields(secondOption));
    }
}
