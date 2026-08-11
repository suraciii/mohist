using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class BuiltInPromptStructureTests
{
    [Fact]
    public void BuiltInPrompts_AllParseWithRequiredFrontmatterFields()
    {
        var templates = new FilePromptLoader().LoadAllTemplates();

        Assert.NotEmpty(templates);
        foreach (var (key, template) in templates)
        {
            Assert.False(string.IsNullOrWhiteSpace(template.DisplayName), $"{key}: DisplayName is required");
            Assert.False(string.IsNullOrWhiteSpace(template.Description), $"{key}: Description is required");
            Assert.False(string.IsNullOrWhiteSpace(template.Body), $"{key}: Body is required");
        }
    }

    [Fact]
    public void IssueQueryBuiltIns_UseCurrentIssueViewCommand()
    {
        var templates = new FilePromptLoader().LoadAllTemplates();
        var queryPromptKeys = new[]
        {
            "apply-feedback",
            "build",
            "design",
            "proposal",
            "review",
            "self-review",
            "specs",
            "tasks",
        };

        Assert.Equal(8, queryPromptKeys.Length);
        Assert.All(templates.Values, template => Assert.DoesNotContain("mo issue show", template.Body));
        foreach (var key in queryPromptKeys)
        {
            Assert.True(templates.TryGetValue(key, out var template), $"Missing builtin prompt: {key}");
            Assert.DoesNotContain("mo issue show", template!.Body);
            Assert.Contains("mo issue view ${{ issue.number }} --project ${{ issue.projectId }}", template.Body);
        }
    }

    [Fact]
    public void FixPrChecksPrompt_UsesProjectPrContextAndFailureError()
    {
        var body = new FilePromptLoader().LoadAllTemplates()["fix-pr-checks"].Body;

        Assert.Contains("${{ vars.github.pr.number }}", body);
        Assert.Contains("${{ vars.github.pr.url }}", body);
        Assert.Contains("${{ failure.error.message }}", body);
        Assert.DoesNotContain("${{ failure.output", body);
    }

    [Fact]
    public void FixPrChecksPrompt_IsStageAgnosticForCheckAndIntegrate()
    {
        var template = new FilePromptLoader().LoadAllTemplates()["fix-pr-checks"];

        Assert.Null(template.Stage);
        Assert.Contains("check", template.Tags);
        Assert.Contains("integrate", template.Tags);

        Assert.DoesNotContain("merge-github-pr rejected", template.Body);
        Assert.DoesNotContain("only checks PR state", template.Body);
        Assert.Contains("gh pr view", template.Body);
        Assert.Contains("gh run view", template.Body);
    }

    [Fact]
    public void DefaultPromptRoot_UsesInjectedApplicationRoot()
    {
        var applicationRoot = Path.Combine(Path.DirectorySeparatorChar.ToString(), "single-file-app");
        var files = new RecordingPromptFileStore();
        var loader = new FilePromptLoader(files: files, applicationRoot: applicationRoot);

        loader.LoadAllTemplates();

        Assert.Equal(
            Path.Combine(applicationRoot, "Workflow", "Services", "Prompts", "builtins"),
            files.DirectoryChecked);
    }

    [Fact]
    public void MissingPromptRoot_FailsClosed()
    {
        var applicationRoot = Path.Combine(Path.DirectorySeparatorChar.ToString(), "missing-single-file-app");
        var loader = new FilePromptLoader(
            files: new RecordingPromptFileStore { DirectoryExistsResult = false },
            applicationRoot: applicationRoot);

        var exception = Assert.Throws<DirectoryNotFoundException>(() => loader.LoadAllTemplates());

        Assert.Contains("builtins", exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingPromptFileStore : IPromptFileStore
    {
        public bool DirectoryExistsResult { get; init; } = true;
        public string? DirectoryChecked { get; private set; }

        public bool DirectoryExists(string path)
        {
            DirectoryChecked = path;
            return DirectoryExistsResult;
        }

        public IEnumerable<string> EnumeratePromptFiles(string path) => [];

        public string ReadAllText(string path) => throw new InvalidOperationException();
    }
}
