using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

public class MohistLocalWorkflowProfileSpecs
{
    [Fact]
    public void IssueWithNonAsciiTitle_BuildsIssueNumberBasedOpenSpecChangeVariables()
    {
        var profile = new MohistLocalIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = "project-1",
            Number = 154,
            Title = "支持中文标题 🚀",
        };

        var variables = profile.BuildVariables("wr-1", issue, new WorkflowProjectContext("project-1", "Mohist", RepositoryBaseBranch: "main"));

        using var document = JsonDocument.Parse(variables);
        Assert.Equal("openspec/changes/issue-154", document.RootElement.GetProperty("openspecChangeDir").GetString());
        Assert.False(document.RootElement.TryGetProperty("artifacts", out _));
    }

    [Fact]
    public void IssueWithNonAsciiTitle_ProjectsIssueNumberBasedChangeDir()
    {
        var state = MohistDefaultWorkflowProjection.ProjectWorkflowState(
            154,
            "支持中文标题 🚀",
            "todo",
            null);

        Assert.Equal("openspec/changes/issue-154", state.ChangeDir);
    }

    [Fact]
    public void AgentConfig_MergesGlobalConfigIntoAgentVariable()
    {
        var profile = new MohistLocalIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = "project-1",
            Number = 1,
            Title = "Agent config",
        };

        var variables = profile.BuildVariables(
            "wr-1",
            issue,
            new WorkflowProjectContext("project-1", "Mohist", RepositoryBaseBranch: "main"),
            new Dictionary<string, object?> { ["model"] = "openai/gpt-4o", ["probeTimeoutMs"] = 30000 });

        using var document = JsonDocument.Parse(variables);
        var agent = document.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-4o", agent.GetProperty("model").GetString());
        Assert.Equal(30000, agent.GetProperty("probeTimeoutMs").GetInt32());
    }

    [Fact]
    public void AgentConfig_WithModelAndVariant_PlacesBothInAgentVariable()
    {
        // Workflow-engine spec: BuildVariables captures the variant alongside
        // the model at issue creation time so per-stage dispatch can carry
        // both. BuildVariables is the source-of-truth seal for this invariant.
        var profile = new MohistLocalIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = "project-1",
            Number = 1,
            Title = "Variant in agent config",
        };

        var variables = profile.BuildVariables(
            "wr-1",
            issue,
            new WorkflowProjectContext("project-1", "Mohist", RepositoryBaseBranch: "main"),
            new Dictionary<string, object?>
            {
                ["model"] = "anthropic/claude-opus-4-20250514",
                ["variant"] = "high",
            });

        using var document = JsonDocument.Parse(variables);
        var agent = document.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("anthropic/claude-opus-4-20250514", agent.GetProperty("model").GetString());
        Assert.Equal("high", agent.GetProperty("variant").GetString());
    }

    [Fact]
    public void StageVariables_MergesStageOverrides()
    {
        var profile = new MohistLocalIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = "project-1",
            Number = 1,
            Title = "Stage vars",
        };

        var stageVariables = profile.BuildStageVariables(
            issue,
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["check"] = new() { ["model"] = "openai/o3" },
            });

        Assert.NotNull(stageVariables);
        Assert.True(stageVariables.ContainsKey("check"));
    }

    [Fact]
    public void BuildVariables_IncludesPromptsFromLoader()
    {
        var loader = new FakePromptLoader();
        var profile = new MohistLocalIssueWorkflowProfile(loader, new FakeDbContextFactory());
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = "project-1",
            Number = 1,
            Title = "Test",
        };

        var variables = profile.BuildVariables("wr-1", issue, new WorkflowProjectContext("project-1", "Mohist", RepositoryBaseBranch: "main"));

        using var document = JsonDocument.Parse(variables);
        var prompts = document.RootElement.GetProperty("prompts");
        Assert.Equal("# Proposal Artifact\nCreate proposal.md", prompts.GetProperty("proposal").GetString());
        Assert.Equal("# Build\nImplement task", prompts.GetProperty("build").GetString());
        Assert.Equal(7, prompts.EnumerateObject().Count());
    }

    [Fact]
    public void BuildVariables_MergesProjectOverridesAndAddsProjectUniqueKeys()
    {
        var loader = new FakePromptLoader();
        var dbFactory = new FakeDbContextFactory(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["proposal"] = "# Project proposal body",
            ["deploy-checklist"] = "# Deploy checklist body",
        }, "project-1");

        var profile = new MohistLocalIssueWorkflowProfile(loader, dbFactory);
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = "project-1",
            Number = 1,
            Title = "Merge test",
        };

        var variables = profile.BuildVariables("wr-1", issue, new WorkflowProjectContext("project-1", "Mohist", RepositoryBaseBranch: "main"));

        using var document = JsonDocument.Parse(variables);
        var prompts = document.RootElement.GetProperty("prompts");
        Assert.Equal("# Project proposal body", prompts.GetProperty("proposal").GetString());
        Assert.Equal("# Build\nImplement task", prompts.GetProperty("build").GetString());
        Assert.Equal("# Deploy checklist body", prompts.GetProperty("deploy-checklist").GetString());
    }

    [Fact]
    public async Task GetMergedPromptsAsync_KeepsSystemBodyWhenNoOverrideExists()
    {
        var loader = new FakePromptLoader();
        var templateStore = new FakeDbContextFactory();
        var profile = new MohistLocalIssueWorkflowProfile(loader, templateStore);

        var merged = await profile.GetMergedPromptsAsync("project-99");

        Assert.Equal("# Build\nImplement task", merged["build"]);
        Assert.Equal(7, merged.Count);
    }

    [Fact]
    public void DefaultIssueWorkflowProfile_DescriptionSourcesFromWorkflowYaml()
    {
        var profile = new MohistLocalIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Equal(MohistWorkflow.ResolveDescription(MohistWorkflow.Definition), profile.Description);
        Assert.Equal(MohistWorkflow.Definition.Description!.TrimEnd(), profile.Description);
    }

    [Fact]
    public void DefaultIssueWorkflowProfile_DescriptionFallsBack_WhenYamlHasNoDescription()
    {
        // Mirrors the spec scenario "Profile without description field":
        // a workflow profile whose source description is missing must
        // surface the "No description provided" fallback string. The
        // MohistLocalIssueWorkflowProfile class applies the fallback
        // through ResolveDescription; the SystemRoutes detail endpoint
        // applies the same string (now sourced from SystemTemplateInfo).
        const string fallback = "No description provided";
        var yamlWithoutDescription = MohistWorkflow.ParseYaml("""
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.Null(yamlWithoutDescription.Description);

        var fallbackDescription = string.IsNullOrWhiteSpace(yamlWithoutDescription.Description)
            ? fallback
            : yamlWithoutDescription.Description!;

        Assert.Equal(fallback, fallbackDescription);
    }

    [Fact]
    public async Task IssueWorkflowProfileRegistry_ListIncludesDescriptionForDefault()
    {
        var loader = new FakePromptLoader();
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueWorkflowProfileRegistry(loader, dbFactory);

        var list = registry.List();

        var defaultEntry = Assert.Single(list, info => info.Id == "mohist/local");
        Assert.True(defaultEntry.IsDefault);
        Assert.Equal(MohistWorkflow.ResolveDescription(MohistWorkflow.Definition), defaultEntry.Description);
    }

    [Fact]
    public async Task ProjectWorkflowProfileManager_SystemTemplates_ExposeDescriptionAndIsDefault()
    {
        var manager = new ProjectWorkflowProfileManager(new FakeDbContextFactory(), new FakePromptLoader(), new PromptTemplateEngine());

        var templates = await manager.ListSystemTemplatesAsync();

        var defaultTemplate = Assert.Single(templates, t => t.Id == "mohist/local");
        Assert.True(defaultTemplate.IsDefault);
        Assert.Equal(MohistWorkflow.ResolveDescription(MohistWorkflow.Definition), defaultTemplate.Description);
    }

    [Fact]
    public void SystemTemplateInfo_ContractCarriesIsDefaultFlag()
    {
        var info = new SystemTemplateInfo("id", "Name", "Desc", true);

        Assert.True(info.IsDefault);
    }

}
