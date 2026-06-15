using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Profile;

public class WorkflowProfileCatalogSpecs
{
    private static IssueWorkflowProfileRegistry BuildRegistry() =>
        new(new FakePromptLoader(), new FakeDbContextFactory());

    private static ProjectWorkflowProfileManager BuildManager() =>
        new(new FakeDbContextFactory(), new FakePromptLoader(), new PromptTemplateEngine());

    // ===================== Profile ID constants =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void IssueWorkflowProfiles_ExposesDefaultSystemId()
    {
        Assert.Equal("mohist/default", IssueWorkflowProfiles.DefaultId);
    }

    // ===================== Registry =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_ListReturnsImplementedProfilesOnly()
    {
        var registry = BuildRegistry();

        var list = registry.List();

        var profile = Assert.Single(list);
        Assert.Equal(IssueWorkflowProfiles.DefaultId, profile.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_DefaultIsMarkedDefault()
    {
        var registry = BuildRegistry();

        var profile = Assert.Single(registry.List());

        Assert.True(profile.IsDefault);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_ProfileDescriptions_ArePresentForAiMatching()
    {
        var registry = BuildRegistry();
        var descriptions = registry.List().Select(info => info.Description).ToList();

        var description = Assert.Single(descriptions);
        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    // ===================== System templates =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SystemTemplates_ListExposesImplementedProfilesOnly()
    {
        var manager = BuildManager();

        var templates = await manager.ListSystemTemplatesAsync();

        var template = Assert.Single(templates);
        Assert.Equal(IssueWorkflowProfiles.DefaultId, template.Id);
    }

    // ===================== GetSystemTemplateDefinition =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void GetSystemTemplateDefinition_QuickFix_ReturnsNullUntilImplemented()
    {
        var def = ProjectWorkflowProfileManager.GetSystemTemplateDefinition("mohist/quick-fix");

        Assert.Null(def);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void GetSystemTemplateDefinition_Experiment_ReturnsNullUntilImplemented()
    {
        var def = ProjectWorkflowProfileManager.GetSystemTemplateDefinition("mohist/experiment");

        Assert.Null(def);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void GetSystemTemplateDefinition_Default_StillWorks()
    {
        var def = ProjectWorkflowProfileManager.GetSystemTemplateDefinition(IssueWorkflowProfiles.DefaultId);

        Assert.NotNull(def);
        Assert.NotEmpty(def!.Stages);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void GetSystemTemplateDefinition_Unknown_ReturnsNull()
    {
        Assert.Null(ProjectWorkflowProfileManager.GetSystemTemplateDefinition("does/not/exist"));
    }

    // ===================== GetSystemTemplateInfo =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void GetSystemTemplateInfo_Default_ReturnsFriendlyDisplayNameAndYamlDescription()
    {
        var info = ProjectWorkflowProfileManager.GetSystemTemplateInfo(IssueWorkflowProfiles.DefaultId);

        Assert.NotNull(info);
        Assert.Equal("Mohist Default", info!.Name);
        Assert.True(info.IsDefault);
        Assert.Equal(MohistWorkflow.Definition.Description, info.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void GetSystemTemplateInfo_QuickFix_ReturnsNullUntilImplemented()
    {
        var info = ProjectWorkflowProfileManager.GetSystemTemplateInfo("mohist/quick-fix");

        Assert.Null(info);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void GetSystemTemplateInfo_Experiment_ReturnsNullUntilImplemented()
    {
        var info = ProjectWorkflowProfileManager.GetSystemTemplateInfo("mohist/experiment");

        Assert.Null(info);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void GetSystemTemplateInfo_Unknown_ReturnsNull()
    {
        Assert.Null(ProjectWorkflowProfileManager.GetSystemTemplateInfo("does/not/exist"));
    }

    // ===================== Detail endpoint metadata =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SystemTemplateInfo_DefaultHasFriendlyNameAndIsDefaultFlag()
    {
        var manager = BuildManager();
        var templates = await manager.ListSystemTemplatesAsync();

        var defaultInfo = Assert.Single(templates);

        Assert.Equal("Mohist Default", defaultInfo.Name);
        Assert.True(defaultInfo.IsDefault);

        Assert.DoesNotContain('/', defaultInfo.Name);
    }
}
