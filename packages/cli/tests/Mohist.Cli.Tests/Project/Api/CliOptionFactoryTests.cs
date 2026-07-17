using System.CommandLine;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Project.Api;

public class CliOptionFactoryTests
{
    [Fact]
    public void ProjectRefOption_ReturnsTwoStringOptionsInCanonicalOrder()
    {
        var (project, projectId) = MohistCliCommands.ProjectRefOption();

        Assert.Equal("--project", project.Name);
        Assert.Equal("--project-id", projectId.Name);
    }

    [Fact]
    public void ProjectRefOption_SharesDescriptionDocumentingCanonicalAndAlias()
    {
        var (project, projectId) = MohistCliCommands.ProjectRefOption();

        Assert.Equal(project.Description, projectId.Description);
        Assert.NotNull(project.Description);
        Assert.Contains("--project", project.Description);
        Assert.Contains("--project-id", project.Description);
        Assert.Contains("backwards-compatible alias", project.Description);
    }

    [Fact]
    public void ProjectIdOption_ReusesProjectRefDescriptionForBackwardsCompatibility()
    {
        var (projectRef, _) = MohistCliCommands.ProjectRefOption();
        var projectId = MohistCliCommands.ProjectIdOption();

        Assert.Equal(projectRef.Description, projectId.Description);
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
