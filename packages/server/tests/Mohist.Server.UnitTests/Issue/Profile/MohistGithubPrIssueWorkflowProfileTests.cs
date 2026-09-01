using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class MohistGithubPrIssueWorkflowProfileTests
{
    private static WorkflowDefinition GithubPrDefinition => WorkflowProfileCatalog.GithubPrWorkflowDefinition;

    private static IssueWorkflowProfileRegistry BuildRegistry() =>
        new();

    [Fact]
    public void IssueWorkflowProfiles_ExposesGithubPrIdConstant()
    {
        Assert.Equal("mohist/github-pr", IssueWorkflowProfiles.GithubPrId);
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_ExposesCorrectMetadata()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile().Profile;

        Assert.Equal("mohist/github-pr", profile.Id);
        Assert.Equal("Mohist GitHub PR", profile.Name);
        Assert.False(string.IsNullOrWhiteSpace(profile.Description));
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_DescriptionSurfacesGhCliPrerequisite()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile().Profile;

        Assert.Contains("gh", profile.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authentication", profile.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHub PR", profile.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_DescriptionReadsFromGithubPrYaml()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile().Profile;

        Assert.Equal(WorkflowProfileCatalog.GithubPrProfileAsset.Description, profile.Description);
        Assert.EndsWith("authentication, and repository auto-merge support.", profile.Description);
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_Definition_ComesFromGithubPrYaml()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile().Profile;

        Assert.Same(GithubPrDefinition, profile.Definition);
        Assert.NotSame(WorkflowProfileCatalog.Definition, profile.Definition);
        Assert.Equal("mohist/github-pr", profile.Id);
    }

    [Fact]
    public void Registry_GetById_ResolvesMohistGithubPr()
    {
        var registry = BuildRegistry();

        var profile = registry.Get("mohist/github-pr");

        Assert.Equal("mohist/github-pr", profile.Id);
        Assert.Same(GithubPrDefinition, profile.Definition);
    }

    [Fact]
    public void Registry_GetById_ResolvesMohistLocal()
    {
        var registry = BuildRegistry();

        var profile = registry.Get("mohist/local");

        Assert.Equal("mohist/local", profile.Id);
        Assert.NotNull(profile.Definition);
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
        Assert.Equal(WorkflowProfileCatalog.Profile.Description, defaultEntry.Description);
        Assert.Equal(WorkflowProfileCatalog.GithubPrProfileAsset.Description, prEntry.Description);
    }

    [Fact]
    public void Registry_Default_StillResolvesToMohistLocal()
    {
        var registry = BuildRegistry();

        var defaultInfo = registry.Default;

        Assert.Equal("mohist/local", defaultInfo.Id);
        Assert.True(defaultInfo.IsDefault);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_ImplementsWorkspaceArtifactsAndAutoMerge()
    {
        var definition = GithubPrDefinition;
        Assert.Equal(["plan", "build", "check", "integrate"], definition.Stages.Select(s => s.Stage).ToArray());
        Assert.Equal(["workspace-prepare", "plan", "push", "open-draft-pr"], definition.Stages[0].Tasks.Select(t => t.Id).ToArray());
        Assert.Equal("mohist/task-list", definition.Stages[1].Tasks.Single(t => t.Id == "load-tasks").Uses);
        Assert.Null(definition.Stages[2].Tasks.Single(t => t.Id == "ai-review").Recovery);
        var integrate = definition.Stages[3];
        Assert.Equal(["workspace-prepare", "push", "enable-auto-merge"], integrate.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal("mohist/enable-github-pr-auto-merge", integrate.Tasks[^1].Uses);
        Assert.Contains(integrate.Tasks[^1].Recovery!.Handlers, h => h.When == "error.code=conflict");
        AssertRepositoryWorkUsesNestedCheckout(definition);
        Assert.DoesNotContain(JsonSerializer.Serialize(definition), "archive-change", StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertRepositoryWorkUsesNestedCheckout(WorkflowDefinition definition)
    {
        var repositoryUses = new HashSet<string>(StringComparer.Ordinal)
        {
            "core/script", "mohist/workspace-prepare", "mohist/rebase", "mohist/push", "mohist/merge-ready",
            "mohist/create-github-pr", "mohist/mark-github-pr-ready", "mohist/github-pr-checks",
            "mohist/github-pr-status", "mohist/enable-github-pr-auto-merge",
        };
        var repositoryDirectory = "REPOS/${{ repository.name }}";
        foreach (var stage in definition.Stages)
        {
            foreach (var task in stage.Tasks)
            {
                AssertTask(task);
                if (task.Recovery is null) continue;
                foreach (var handler in task.Recovery.Handlers)
                    foreach (var recoveryTask in handler.Tasks)
                        AssertTask(recoveryTask);
            }
            foreach (var check in stage.Checks.Where(check => repositoryUses.Contains(check.Uses)))
                Assert.Equal(repositoryDirectory, check.With!["working-directory"]!.Value.GetString());
        }

        void AssertTask(TaskDefinition task)
        {
            if (repositoryUses.Contains(task.Uses))
                Assert.Equal(repositoryDirectory, task.With!["working-directory"]!.Value.GetString());
        }
    }
}
