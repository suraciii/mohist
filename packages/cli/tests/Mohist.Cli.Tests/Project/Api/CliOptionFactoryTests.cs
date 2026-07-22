using System.CommandLine;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Project.Api;

public class CliOptionFactoryTests
{
    [Fact]
    public void ProjectRefOption_ReturnsCanonicalProjectAndDoesNotAdvertiseLegacyOption()
    {
        var (project, projectId) = MohistCliCommands.ProjectRefOption();

        Assert.Equal("--project", project.Name);
        Assert.Equal("--project-id", projectId.Name);
        Assert.True(projectId.Hidden);
    }

    [Fact]
    public void ProjectRefOption_DescribesProjectReference()
    {
        var (project, projectId) = MohistCliCommands.ProjectRefOption();

        Assert.NotNull(project.Description);
        Assert.Contains("Project name or id", project.Description);
        Assert.DoesNotContain("--project", project.Description);
    }

    [Fact]
    public void ProjectIdOption_IsNotAdvertisedByTheCanonicalFactory()
    {
        var projectId = MohistCliCommands.ProjectIdOption();

        Assert.True(projectId.Hidden);
    }

    [Fact]
    public void OutputOption_DefaultsToHumanOutput()
    {
        var output = MohistCliCommands.OutputOption();

        Assert.NotNull(output.DefaultValueFactory);
        var defaultValue = output.DefaultValueFactory(default!);

        Assert.Equal("table", defaultValue);
    }

    [Fact]
    public void OutputOption_DescriptionDocumentsFieldSelectionAndDiscovery()
    {
        var output = MohistCliCommands.OutputOption();

        Assert.NotNull(output.Description);
        Assert.Contains("selected fields", output.Description);
        Assert.Contains("list available fields", output.Description);
    }

    [Fact]
    public void OutputOption_IsNamedJson()
    {
        var output = MohistCliCommands.OutputOption();

        Assert.Equal("--json", output.Name);
    }
}
