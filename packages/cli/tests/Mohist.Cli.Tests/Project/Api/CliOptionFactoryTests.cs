using System.CommandLine;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Project.Api;

public class CliOptionFactoryTests
{
    private static readonly ResourceDescriptor OutputDescriptor = new(ResourceCardinality.Single, ["id"]);

    [Fact]
    public void ProjectRefOption_ReturnsCanonicalProjectAndDoesNotAdvertiseLegacyOption()
    {
        var project = MohistCliCommands.ProjectRefOption();

        Assert.Equal("--project", project.Name);
    }

    [Fact]
    public void ProjectRefOption_DescribesProjectReference()
    {
        var project = MohistCliCommands.ProjectRefOption();

        Assert.NotNull(project.Description);
        Assert.Contains("Project name or id", project.Description);
        Assert.DoesNotContain("--project", project.Description);
    }

    [Fact]
    public void OutputOption_DefaultsToHumanOutput()
    {
        var output = MohistCliCommands.OutputOption(OutputDescriptor);

        Assert.NotNull(output.DefaultValueFactory);
        var defaultValue = output.DefaultValueFactory(default!);

        Assert.Equal("table", defaultValue);
    }

    [Fact]
    public void OutputOption_DescriptionDocumentsFieldSelectionAndDiscovery()
    {
        var output = MohistCliCommands.OutputOption(OutputDescriptor);

        Assert.NotNull(output.Description);
        Assert.Contains("selected fields", output.Description);
        Assert.Contains("list available fields", output.Description);
    }

    [Fact]
    public void OutputOption_IsNamedJson()
    {
        var output = MohistCliCommands.OutputOption(OutputDescriptor);

        Assert.Equal("--json", output.Name);
    }
}
