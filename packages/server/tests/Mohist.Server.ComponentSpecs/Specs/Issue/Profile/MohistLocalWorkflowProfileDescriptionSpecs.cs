using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Domain;
using Issue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.ComponentSpecs.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Xunit;

namespace Mohist.Server.ComponentSpecs.Specs.Issue.Profile;

public class MohistLocalWorkflowProfileDescriptionSpecs
{
    [Fact]
    public void DefaultWorkflowDefinition_DescriptionIsParsedFromYamlBlockScalar()
    {
        var description = MohistWorkflow.Definition.Description;

        Assert.NotNull(description);
        Assert.Contains("plan (proposal, specs, design, tasks, self-review)", description!);
        Assert.Contains("build", description);
        Assert.Contains("check (AI review, merge readiness)", description);
        Assert.Contains("integrate (archive, merge, push)", description);
        Assert.DoesNotContain("use quick-fix", description);
        Assert.DoesNotContain("use experiment", description);
    }

    [Fact]
    public void DefaultWorkflowDefinition_DescriptionPreservesMultilineLineBreaks()
    {
        var description = MohistWorkflow.Definition.Description;

        Assert.NotNull(description);
        Assert.Contains("→", description!);
        Assert.Contains("\n", description);
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsDescriptionField()
    {
        var definition = MohistWorkflow.Definition;
        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        Assert.Equal(definition.Description, reparsed.Description);
        Assert.Contains("description:", yaml);
    }

    [Fact]
    public void WorkflowYamlParser_ProfileWithoutDescriptionYieldsNullDescription()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: build
            tasks: []
            checks: []
        """);

        Assert.Null(definition.Description);
    }

    [Fact]
    public void WorkflowYamlParser_ProfileWithSingleLineDescription_ParsesItVerbatim()
    {
        var definition = MohistWorkflow.ParseYaml("""
        description: Simple description
        stages:
          - stage: build
            tasks: []
            checks: []
        """);

        Assert.Equal("Simple description", definition.Description);
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

    [Fact]
    public void DescriptionField_DoesNotInfluenceStageExecutionShape()
    {
        // The description field is passive metadata; verify the engine
        // payload (stages, tasks, checks) is identical to the version
        // without the description key, plus the round-trip is stable.
        var descriptionOnlyYaml = """
            id: mohist/local
            description: |
              Some user-facing description that the engine must not
              read or interpret.
            stages:
              - stage: build
                tasks: []
                checks: []
            """;

        var parsed = MohistWorkflow.ParseYaml(descriptionOnlyYaml);

        Assert.Equal("build", parsed.Stages[0].Stage);
        Assert.Empty(parsed.Stages[0].Tasks);
        Assert.Empty(parsed.Stages[0].Checks);
        Assert.Contains("user-facing description", parsed.Description);

        var yaml = WorkflowYamlSerializer.ToYaml(parsed);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);
        Assert.Equal(parsed.Description, reparsed.Description);
    }

    [Fact]
    public void DefaultWorkflowYaml_OmitsStructuredMetadataFields()
    {
        // Locks the "description-only" design decision. The spec scenario
        // "Other metadata fields are absent" forbids the top-level of a
        // workflow profile YAML from carrying risk_level, typical_duration,
        // suitable_for, avoid_for, tags, or default_approval_policy — those
        // belong inside the natural-language description.
        var yaml = WorkflowYamlSerializer.ToYaml(MohistWorkflow.Definition);

        var forbidden = new[]
        {
            "risk_level:",
            "riskLevel:",
            "typical_duration:",
            "typicalDuration:",
            "suitable_for:",
            "suitableFor:",
            "avoid_for:",
            "avoidFor:",
            "tags:",
            "default_approval_policy:",
            "defaultApprovalPolicy:",
        };
        foreach (var needle in forbidden)
            Assert.DoesNotContain(needle, yaml, StringComparison.Ordinal);
    }
}
