using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

public class WorkflowProfileCatalogSpecs
{
    [Fact]
    public void Registry_ListsImplementedBuiltInsAndMarksLocalDefault()
    {
        var profiles = new IssueWorkflowProfileRegistry().List();

        Assert.Equal(2, profiles.Count);
        Assert.Single(profiles, profile => profile.Id == IssueWorkflowProfiles.LocalId && profile.IsDefault);
        Assert.Contains(profiles, profile => profile.Id == IssueWorkflowProfiles.GithubPrId);
    }

    [Fact]
    public void Catalog_ProvidesBuiltInMetadataAndDefinitions()
    {
        var local = WorkflowProfileCatalog.Profile;
        var githubPr = WorkflowProfileCatalog.GithubPrProfileAsset;

        Assert.Equal("Mohist Local", local.Name);
        Assert.Equal("Mohist GitHub PR", githubPr.Name);
        Assert.NotEmpty(local.Definition.Stages);
        Assert.NotEmpty(githubPr.Definition.Stages);
    }

    [Fact]
    public void Catalog_OnlyResolvesImplementedProfiles()
    {
        Assert.Null(WorkflowProfileCatalog.GetDefinition("mohist/quick-fix"));
        Assert.Null(WorkflowProfileCatalog.GetDefinition("mohist/experiment"));
        Assert.Null(WorkflowProfileCatalog.GetDefinition("does/not/exist"));
    }
}
