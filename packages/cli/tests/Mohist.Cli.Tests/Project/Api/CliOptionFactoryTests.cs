using System.CommandLine;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Project.Api;

public class CliOptionFactoryTests
{
    [Fact]
    public void ProjectRefOption_ReturnsCanonicalProjectAndHiddenLegacyOption()
    {
        var (project, projectId) = MohistCliCommands.ProjectRefOption();

        Assert.Equal("--project", project.Name);
        Assert.Equal("--project", projectId.Name);
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
    public void OutputOption_DefaultsToJson()
    {
        var output = MohistCliCommands.OutputOption();

        Assert.NotNull(output.DefaultValueFactory);
        var defaultValue = output.DefaultValueFactory(default!);

        Assert.Equal("json", defaultValue);
    }

    [Fact]
    public void OutputOption_DescriptionDocumentsTableAndJson()
    {
        var output = MohistCliCommands.OutputOption();

        Assert.NotNull(output.Description);
        Assert.Contains("table", output.Description);
        Assert.Contains("json", output.Description);
    }

    [Fact]
    public void OutputOption_IsNamedOutput()
    {
        var output = MohistCliCommands.OutputOption();

        Assert.Equal("--output", output.Name);
    }
}
