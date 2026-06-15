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
    public void IssueWorkflowProfiles_ExposesAllThreeSystemIds()
    {
        Assert.Equal("mohist/default", IssueWorkflowProfiles.DefaultId);
        Assert.Equal("mohist/quick-fix", IssueWorkflowProfiles.QuickFixId);
        Assert.Equal("mohist/experiment", IssueWorkflowProfiles.ExperimentId);
    }

    // ===================== Registry =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_ListReturnsAllThreeProfiles()
    {
        var registry = BuildRegistry();

        var list = registry.List();

        Assert.Equal(3, list.Count);
        Assert.Contains(list, info => info.Id == IssueWorkflowProfiles.DefaultId);
        Assert.Contains(list, info => info.Id == IssueWorkflowProfiles.QuickFixId);
        Assert.Contains(list, info => info.Id == IssueWorkflowProfiles.ExperimentId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_DefaultIsMarkedDefault_OtherTwoAreNot()
    {
        var registry = BuildRegistry();

        var list = registry.List();
        var byId = list.ToDictionary(info => info.Id, StringComparer.OrdinalIgnoreCase);

        Assert.True(byId[IssueWorkflowProfiles.DefaultId].IsDefault);
        Assert.False(byId[IssueWorkflowProfiles.QuickFixId].IsDefault);
        Assert.False(byId[IssueWorkflowProfiles.ExperimentId].IsDefault);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_QuickFix_HasDistinctDescription()
    {
        var profile = new MohistQuickFixIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Equal(IssueWorkflowProfiles.QuickFixId, profile.Id);
        Assert.Equal("Mohist Quick Fix", profile.DisplayName);
        Assert.False(profile.IsDefault);
        Assert.NotEqual(profile.Description, new MohistDefaultIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory()).Description);
        // Spec: description SHALL indicate suitability for simple bug fixes and small, low-risk changes
        Assert.Contains("bug fix", profile.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("low-risk", profile.Description, StringComparison.OrdinalIgnoreCase);
        // Spec: describe faster, lighter workflow with fewer approvals
        Assert.True(
            profile.Description.Contains("fast", StringComparison.OrdinalIgnoreCase)
            || profile.Description.Contains("lightweight", StringComparison.OrdinalIgnoreCase)
            || profile.Description.Contains("lighter", StringComparison.OrdinalIgnoreCase),
            $"Quick-fix description should indicate faster/lighter workflow. Was: {profile.Description}");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_Experiment_HasDistinctDescription()
    {
        var profile = new MohistExperimentIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Equal(IssueWorkflowProfiles.ExperimentId, profile.Id);
        Assert.Equal("Mohist Experiment", profile.DisplayName);
        Assert.False(profile.IsDefault);
        Assert.NotEqual(profile.Description, new MohistDefaultIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory()).Description);
        // Spec: description SHALL indicate suitability for exploration, prototyping, and spikes
        Assert.True(
            profile.Description.Contains("explor", StringComparison.OrdinalIgnoreCase)
            || profile.Description.Contains("prototyp", StringComparison.OrdinalIgnoreCase)
            || profile.Description.Contains("spike", StringComparison.OrdinalIgnoreCase),
            $"Experiment description should mention exploration/prototyping/spike. Was: {profile.Description}");
        // Spec: indicate that deliverable artifacts are not required
        Assert.Contains("no deliverable artifacts", profile.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_QuickFixAndExperiment_ReuseDefaultDefinitionStages()
    {
        var defaultDefinition = new MohistDefaultIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory()).Definition;
        var quickFixDefinition = new MohistQuickFixIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory()).Definition;
        var experimentDefinition = new MohistExperimentIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory()).Definition;

        Assert.Equal(defaultDefinition.Id, quickFixDefinition.Id);
        Assert.Equal(defaultDefinition.Id, experimentDefinition.Id);
        Assert.Equal(defaultDefinition.Stages.Count, quickFixDefinition.Stages.Count);
        Assert.Equal(defaultDefinition.Stages.Count, experimentDefinition.Stages.Count);
        for (var i = 0; i < defaultDefinition.Stages.Count; i++)
        {
            Assert.Equal(defaultDefinition.Stages[i].Stage, quickFixDefinition.Stages[i].Stage);
            Assert.Equal(defaultDefinition.Stages[i].Stage, experimentDefinition.Stages[i].Stage);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_ProfileDescriptions_AreSufficientlyDistinctForAiMatching()
    {
        var registry = BuildRegistry();
        var descriptions = registry.List().Select(info => info.Description).ToList();

        Assert.Equal(3, descriptions.Count);
        // All three descriptions must be non-empty
        Assert.All(descriptions, d => Assert.False(string.IsNullOrWhiteSpace(d)));
        // All three descriptions must differ pairwise
        Assert.NotEqual(descriptions[0], descriptions[1]);
        Assert.NotEqual(descriptions[0], descriptions[2]);
        Assert.NotEqual(descriptions[1], descriptions[2]);
    }

    // ===================== System templates =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SystemTemplates_ListExposesAllThreeProfiles()
    {
        var manager = BuildManager();

        var templates = await manager.ListSystemTemplatesAsync();

        Assert.Equal(3, templates.Count);
        Assert.Contains(templates, t => t.Id == IssueWorkflowProfiles.DefaultId);
        Assert.Contains(templates, t => t.Id == IssueWorkflowProfiles.QuickFixId);
        Assert.Contains(templates, t => t.Id == IssueWorkflowProfiles.ExperimentId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SystemTemplates_QuickFixEntryCarriesDistinctDescriptionAndIsDefaultFalse()
    {
        var manager = BuildManager();

        var templates = await manager.ListSystemTemplatesAsync();
        var quickFix = Assert.Single(templates, t => t.Id == IssueWorkflowProfiles.QuickFixId);

        Assert.Equal("Mohist Quick Fix", quickFix.Name);
        Assert.False(quickFix.IsDefault);
        Assert.Equal(MohistQuickFixIssueWorkflowProfile.HardcodedDescription, quickFix.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SystemTemplates_ExperimentEntryCarriesDistinctDescriptionAndIsDefaultFalse()
    {
        var manager = BuildManager();

        var templates = await manager.ListSystemTemplatesAsync();
        var experiment = Assert.Single(templates, t => t.Id == IssueWorkflowProfiles.ExperimentId);

        Assert.Equal("Mohist Experiment", experiment.Name);
        Assert.False(experiment.IsDefault);
        Assert.Equal(MohistExperimentIssueWorkflowProfile.HardcodedDescription, experiment.Description);
    }

    // ===================== GetSystemTemplateDefinition =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void GetSystemTemplateDefinition_QuickFix_ReturnsDefaultStages()
    {
        var def = ProjectWorkflowProfileManager.GetSystemTemplateDefinition(IssueWorkflowProfiles.QuickFixId);

        Assert.NotNull(def);
        Assert.Equal(MohistWorkflow.Definition.Id, def!.Id);
        Assert.NotEmpty(def.Stages);
        Assert.Contains(def.Stages, s => s.Stage == "plan");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void GetSystemTemplateDefinition_Experiment_ReturnsDefaultStages()
    {
        var def = ProjectWorkflowProfileManager.GetSystemTemplateDefinition(IssueWorkflowProfiles.ExperimentId);

        Assert.NotNull(def);
        Assert.Equal(MohistWorkflow.Definition.Id, def!.Id);
        Assert.NotEmpty(def.Stages);
        Assert.Contains(def.Stages, s => s.Stage == "plan");
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
    public void GetSystemTemplateInfo_QuickFix_ReturnsQuickFixDisplayNameAndHardcodedDescription()
    {
        var info = ProjectWorkflowProfileManager.GetSystemTemplateInfo(IssueWorkflowProfiles.QuickFixId);

        Assert.NotNull(info);
        Assert.Equal("Mohist Quick Fix", info!.Name);
        Assert.False(info.IsDefault);
        Assert.Equal(MohistQuickFixIssueWorkflowProfile.HardcodedDescription, info.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void GetSystemTemplateInfo_Experiment_ReturnsExperimentDisplayNameAndHardcodedDescription()
    {
        var info = ProjectWorkflowProfileManager.GetSystemTemplateInfo(IssueWorkflowProfiles.ExperimentId);

        Assert.NotNull(info);
        Assert.Equal("Mohist Experiment", info!.Name);
        Assert.False(info.IsDefault);
        Assert.Equal(MohistExperimentIssueWorkflowProfile.HardcodedDescription, info.Description);
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
    public async Task SystemTemplateInfo_ForEachProfile_HasDistinctNameAndIsDefaultFlag()
    {
        // The detail endpoint must surface the per-profile display name and
        // isDefault flag from SystemTemplateInfo (not derive from id). The
        // mohist/default entry is the only default; quick-fix and experiment
        // expose their friendly display name (not the raw id).
        var manager = BuildManager();
        var templates = await manager.ListSystemTemplatesAsync();

        var defaultInfo = Assert.Single(templates, t => t.Id == IssueWorkflowProfiles.DefaultId);
        var quickFixInfo = Assert.Single(templates, t => t.Id == IssueWorkflowProfiles.QuickFixId);
        var experimentInfo = Assert.Single(templates, t => t.Id == IssueWorkflowProfiles.ExperimentId);

        Assert.Equal("Mohist Default", defaultInfo.Name);
        Assert.Equal("Mohist Quick Fix", quickFixInfo.Name);
        Assert.Equal("Mohist Experiment", experimentInfo.Name);
        Assert.True(defaultInfo.IsDefault);
        Assert.False(quickFixInfo.IsDefault);
        Assert.False(experimentInfo.IsDefault);

        // Display names are user-facing: they must not contain the slash separator
        // and must be unique across profiles.
        Assert.DoesNotContain('/', defaultInfo.Name);
        Assert.DoesNotContain('/', quickFixInfo.Name);
        Assert.DoesNotContain('/', experimentInfo.Name);
    }
}
