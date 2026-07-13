using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class MohistGithubPrWorkflowProfileRegistryTests
{
    private static IssueWorkflowProfileRegistry BuildRegistry() =>
        new(new FakePromptLoader(), new FakeDbContextFactory());

    [Fact]
    public void Registry_GetById_ResolvesMohistGithubPr()
    {
        var registry = BuildRegistry();

        var profile = registry.Get("mohist/github-pr");

        Assert.Equal("mohist/github-pr", profile.Id);
        Assert.False(profile.IsDefault);
        Assert.Same(MohistWorkflow.GithubPrWorkflowDefinition, profile.Definition);
    }

    [Fact]
    public void Registry_GetById_ResolvesMohistLocal()
    {
        var registry = BuildRegistry();

        var profile = registry.Get("mohist/local");

        Assert.Equal("mohist/local", profile.Id);
        Assert.True(profile.IsDefault);
    }

    [Fact]
    public void Registry_GetByNullOrEmpty_ResolvesMohistLocal()
    {
        var registry = BuildRegistry();

        var byNull = registry.Get(null);
        var byEmpty = registry.Get(string.Empty);
        var byWhitespace = registry.Get("   ");

        Assert.Equal("mohist/local", byNull.Id);
        Assert.Equal("mohist/local", byEmpty.Id);
        Assert.Equal("mohist/local", byWhitespace.Id);
    }

    [Fact]
    public void Registry_Exists_RecognizesMohistGithubPr()
    {
        var registry = BuildRegistry();

        Assert.True(registry.Exists("mohist/github-pr"));
        Assert.True(registry.Exists("mohist/local"));
        Assert.False(registry.Exists("mohist/pr"));
        Assert.False(registry.Exists("mohist/unknown"));
    }

    [Fact]
    public void Registry_ListIncludesBothBuiltInProfilesWithExpectedMetadata()
    {
        var registry = BuildRegistry();

        var list = registry.List();

        Assert.Equal(2, list.Count);
        var defaultEntry = Assert.Single(list, info => info.Id == "mohist/local");
        var prEntry = Assert.Single(list, info => info.Id == "mohist/github-pr");

        Assert.True(defaultEntry.IsDefault);
        Assert.False(prEntry.IsDefault);
        Assert.False(string.IsNullOrWhiteSpace(defaultEntry.Description));
        Assert.False(string.IsNullOrWhiteSpace(prEntry.Description));
        Assert.DoesNotContain(list, info => info.Id == "mohist/pr");
    }

    [Fact]
    public void Registry_ListDescribed_ExposesDescriptionForBothBuiltIns()
    {
        var registry = BuildRegistry();

        var described = registry.ListDescribed();

        Assert.Equal(2, described.Count);
        var defaultEntry = Assert.Single(described, d => d.Id == "mohist/local");
        var prEntry = Assert.Single(described, d => d.Id == "mohist/github-pr");

        Assert.False(string.IsNullOrWhiteSpace(defaultEntry.Description));
        Assert.False(string.IsNullOrWhiteSpace(prEntry.Description));
        Assert.Equal(MohistWorkflow.ResolveDescription(MohistWorkflow.Definition), defaultEntry.Description);
        Assert.Equal(MohistWorkflow.ResolveDescription(MohistWorkflow.GithubPrWorkflowDefinition), prEntry.Description);
    }

    [Fact]
    public void Registry_Default_StillResolvesToMohistLocal()
    {
        var registry = BuildRegistry();

        var defaultInfo = registry.Default;

        Assert.Equal("mohist/local", defaultInfo.Id);
        Assert.True(defaultInfo.IsDefault);
    }

    // ===================== Full graph / action names =====================

    [Fact]
    public void ProjectWorkflowProfileManager_GetSystemTemplateInfo_GithubPr_ReturnsFriendlyDisplayName()
    {
        var info = ProjectWorkflowProfileManager.GetSystemTemplateInfo("mohist/github-pr");

        Assert.NotNull(info);
        Assert.Equal("Mohist GitHub PR", info!.Name);
        Assert.False(info.IsDefault);
        Assert.Contains("gh auth login", info.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectWorkflowProfileManager_GetSystemTemplateDefinition_GithubPr_ReturnsGithubPrDefinition()
    {
        var def = ProjectWorkflowProfileManager.GetSystemTemplateDefinition("mohist/github-pr");

        Assert.NotNull(def);
        Assert.Same(MohistWorkflow.GithubPrWorkflowDefinition, def);
    }

    [Fact]
    public void ProjectWorkflowProfileManager_GetSystemTemplateInfo_LegacyPrId_ReturnsNull()
    {
        Assert.Null(ProjectWorkflowProfileManager.GetSystemTemplateInfo("mohist/pr"));
        Assert.Null(ProjectWorkflowProfileManager.GetSystemTemplateDefinition("mohist/pr"));
    }

    [Fact]
    public async Task ProjectWorkflowProfileManager_SystemTemplates_ExposeGithubPrTemplate()
    {
        var manager = new ProjectWorkflowProfileManager(new FakeDbContextFactory(), new FakePromptLoader(), new PromptTemplateEngine());

        var templates = await manager.ListSystemTemplatesAsync();

        var prTemplate = Assert.Single(templates, t => t.Id == "mohist/github-pr");
        Assert.Equal("Mohist GitHub PR", prTemplate.Name);
        Assert.False(prTemplate.IsDefault);
        Assert.Contains("gh auth login", prTemplate.Description, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(templates, t => t.Id == "mohist/pr");
    }

    [Fact]
    public async Task BothCatalogPaths_AgreeWithProfileInstanceForBothBuiltIns()
    {
        var registry = BuildRegistry();
        var manager = new ProjectWorkflowProfileManager(new FakeDbContextFactory(), new FakePromptLoader(), new PromptTemplateEngine());

        var templates = await manager.ListSystemTemplatesAsync();
        var described = registry.ListDescribed();

        foreach (var profileId in new[] { IssueWorkflowProfiles.LocalId, IssueWorkflowProfiles.GithubPrId })
        {
            var profile = registry.Get(profileId);
            var template = Assert.Single(templates, t => t.Id == profileId);
            var describedEntry = Assert.Single(described, d => d.Id == profileId);

            Assert.Equal(profile.Description, template.Description);
            Assert.Equal(profile.Description, describedEntry.Description);
        }
    }

    // ===================== Helpers =====================
}
