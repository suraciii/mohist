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

public class MohistLocalWorkflowProfilePromptSpecs
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

    private sealed class FakePromptFileStore : IPromptFileStore
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
}
