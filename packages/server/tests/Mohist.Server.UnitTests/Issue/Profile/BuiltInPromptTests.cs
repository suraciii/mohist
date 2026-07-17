using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class BuiltInPromptTests
{
    [Fact]
    public void DefaultPrompts_DefineWorkflowArtifactBoundaryForReviewAndAutoFix()
    {
        var files = new FakePromptFileStore("/prompts");
        files.Add("review.prompt", """
            Mohist workflow artifacts under `${{ openspecChangeDir }}/` are review context and evidence, not product deliverables by themselves.
            Do not fail solely because `${{ openspecChangeDir }}/proposal.md`, `design.md`, `tasks.json`, `self-review.md`, `review.md`, or delta specs exist.
            """);
        files.Add("auto-fix.prompt", """
            Do NOT remove Mohist workflow artifacts under `${{ openspecChangeDir }}/`.
            Workflow artifacts under `${{ openspecChangeDir }}/` are planning/review context, not product deliverables to delete during auto-fix.
            """);

        var loader = new FilePromptLoader("/prompts", files);
        var prompts = loader.LoadAll();

        Assert.Contains("workflow artifacts", prompts["review"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not product deliverables", prompts["review"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("${{ openspecChangeDir }}/proposal.md", prompts["review"], StringComparison.Ordinal);
        Assert.Contains("do not remove mohist workflow artifacts", prompts["auto-fix"].ToLowerInvariant());
        Assert.Contains("${{ openspecChangeDir }}/", prompts["auto-fix"], StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPrompts_LoadIssueDetailsThroughMohistCli()
    {
        var loader = new FilePromptLoader();
        var prompts = loader.LoadAll();

        const string command = "mo issue show ${{ issue.number }} --project-id ${{ project.id }}";
        var executionPrompts = prompts.Keys.ToArray();
        Assert.NotEmpty(executionPrompts);
        foreach (var key in executionPrompts)
        {
            Assert.Contains(command, prompts[key], StringComparison.Ordinal);
            Assert.DoesNotContain("prompts.issue-context", prompts[key], StringComparison.Ordinal);
        }

        var variablesJson = JsonSerializer.Serialize(new
        {
            issue = new { number = 42 },
            project = new { id = "project-1" },
            openspecChangeDir = "openspec/changes/issue-42",
        });
        using var variables = JsonDocument.Parse(variablesJson);
        var (rendered, missing, _) = new PromptTemplateEngine().Render(prompts["proposal"], variables.RootElement);

        Assert.Empty(missing);
        Assert.Contains("mo issue show 42 --project-id project-1", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("prompts.issue-context", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInApplyFeedbackPrompt_HasRequiredFrontmatterFields()
    {
        var loader = new FilePromptLoader();
        var prompts = loader.LoadAll();

        Assert.True(prompts.ContainsKey("apply-feedback"), "apply-feedback prompt must be loaded from builtins");
        var body = prompts["apply-feedback"];
        Assert.Contains("mo issue feedback show", body, StringComparison.Ordinal);
        Assert.Contains("${{ issue.number }}", body, StringComparison.Ordinal);
        Assert.Contains("${{ project.id }}", body, StringComparison.Ordinal);
        Assert.Contains("${{ approvalFeedback.id }}", body, StringComparison.Ordinal);
        Assert.Contains("${{ approvalFeedback.command }}", body, StringComparison.Ordinal);
        Assert.Contains("${{ stage.name }}", body, StringComparison.Ordinal);
        Assert.Contains("Do not approve the stage", body, StringComparison.Ordinal);
        Assert.Contains("required input", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resolution summary", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuiltInApplyFeedbackPrompt_FrontmatterParsesCleanly()
    {
        var loader = new FilePromptLoader();
        var templates = loader.LoadAllTemplates();
        var template = templates["apply-feedback"];

        Assert.Equal("apply-feedback", template.Key);
        Assert.Equal("Apply Approval Feedback", template.DisplayName);
        Assert.Contains("approval feedback", template.Description, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(template.Tags);
        Assert.Equal("approval", template.Stage);
        Assert.Contains("mo issue feedback show", template.Body, StringComparison.Ordinal);
        Assert.Contains("Do not approve the stage", template.Body, StringComparison.Ordinal);
    }
}

internal sealed class FakePromptFileStore : IPromptFileStore
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public FakePromptFileStore(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public void Add(string name, string content) => _files[Path.Combine(Root, name)] = content;

    public bool DirectoryExists(string path) => path == Root;

    public IEnumerable<string> EnumeratePromptFiles(string path) =>
        path == Root ? _files.Keys.Where(k => k.EndsWith(".prompt", StringComparison.Ordinal)).Order() : [];

    public string ReadAllText(string path) => _files[path];
}
