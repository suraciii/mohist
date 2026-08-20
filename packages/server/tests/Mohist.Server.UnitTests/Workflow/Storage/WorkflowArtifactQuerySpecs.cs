using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Storage;

/// <summary>
/// Calculation specs for the workflow-artifact read path behind
/// <c>GET /api/.../workflow/artifacts</c> (list) and
/// <c>.../artifacts/&#123;id&#125;/content</c> (directory content): the
/// <c>WorkflowArtifactQuerier</c> latest-per-path / path-history /
/// no-history-latest / task-run filtering and the
/// <c>IWorkflowArtifactStorage</c> directory listing and entry bytes. Both
/// run against MohistDbFixture without an HTTP round-trip. The route
/// contract (file content stream, cross-issue rejection, DTO naming) stays
/// in <c>WorkflowArtifactQueryRouteSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class WorkflowArtifactQuerySpecs
{
    private readonly MohistDbFixture _fixture;

    public WorkflowArtifactQuerySpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private static readonly DateTimeOffset BaseTime = new(2026, 6, 11, 10, 0, 0, TimeSpan.Zero);

    private IWorkflowArtifactQuerier CreateQuerier() =>
        _fixture.Services.GetRequiredService<IWorkflowArtifactQuerier>();

    private IWorkflowArtifactStorage CreateStorage() =>
        _fixture.Services.GetRequiredService<IWorkflowArtifactStorage>();

    /// <summary>
    /// Seeds the same cross-path artifact set the HTTP spec uses: a
    /// proposal file, two review.md versions, and a specs/ directory, all
    /// under one workflow run.
    /// </summary>
    private async Task<string> SeedArtifactsAsync()
    {
        var workflowRunId = $"wr_artq_{Guid.NewGuid():N}";
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();

        db.WorkflowArtifacts.AddRange(
            Row(workflowRunId, "proposal.1", "openspec/changes/issue-55/proposal.md", "file", BaseTime),
            Row(workflowRunId, "ai-review.1", "review.md", "file", BaseTime.AddMinutes(5)),
            Row(workflowRunId, "ai-review.2", "review.md", "file", BaseTime.AddMinutes(10)),
            Row(workflowRunId, "design.1", "specs/", "directory", BaseTime.AddMinutes(2)));
        await db.SaveChangesAsync();
        return workflowRunId;
    }

    private static WorkflowArtifactRow Row(string workflowRunId, string taskRunId, string path, string kind, DateTimeOffset recordedAt) => new()
    {
        ArtifactId = $"art_{taskRunId}_{Guid.NewGuid():N}",
        WorkflowRunId = workflowRunId,
        TaskRunId = taskRunId,
        Path = path,
        RecordedAt = recordedAt,
        ArtifactStoragePath = $"{workflowRunId}/tasks/{taskRunId}/artifacts/{taskRunId}/content",
        Kind = kind,
        ContentType = "text/markdown",
        Size = 100,
        DisplayName = path.TrimEnd('/'),
    };

    [Fact]
    public async Task ListLatestAsync_ReturnsLatestPerPath()
    {
        var workflowRunId = await SeedArtifactsAsync();
        var querier = CreateQuerier();

        var latest = await querier.ListLatestAsync(workflowRunId);

        Assert.Equal(3, latest.Count);
        var review = Assert.Single(latest, a => a.Path == "review.md");
        Assert.Equal("ai-review.2", review.TaskRunId);
        Assert.Equal("file", review.Kind);
    }

    [Fact]
    public async Task ListHistoryAsync_ReturnsAllVersionsInRecordedOrder()
    {
        var workflowRunId = await SeedArtifactsAsync();
        var querier = CreateQuerier();

        var history = await querier.ListHistoryAsync(workflowRunId, "review.md");

        Assert.Equal(2, history.Count);
        Assert.Equal("ai-review.1", history[0].TaskRunId);
        Assert.Equal("ai-review.2", history[1].TaskRunId);
    }

    [Fact]
    public async Task ListLatestByPathAsync_ReturnsOnlyTheNewestVersion()
    {
        var workflowRunId = await SeedArtifactsAsync();
        var querier = CreateQuerier();

        var latest = await querier.ListLatestByPathAsync(workflowRunId, "review.md");

        var single = Assert.Single(latest);
        Assert.Equal("review.md", single.Path);
        Assert.Equal("ai-review.2", single.TaskRunId);
    }

    [Fact]
    public async Task ListByTaskRunAsync_ReturnsArtifactsProducedByThatTask()
    {
        var workflowRunId = await SeedArtifactsAsync();
        var querier = CreateQuerier();

        var produced = await querier.ListByTaskRunAsync(workflowRunId, "ai-review.1");

        var single = Assert.Single(produced);
        Assert.Equal("ai-review.1", single.TaskRunId);
        Assert.Equal("review.md", single.Path);
    }

    [Fact]
    public async Task ListLatestAsync_WorkflowRunWithoutArtifacts_ReturnsEmpty()
    {
        var querier = CreateQuerier();

        var latest = await querier.ListLatestAsync($"wr_empty_{Guid.NewGuid():N}");

        Assert.Empty(latest);
    }

    [Fact]
    public async Task ListLatestAsync_DirectoryArtifactAppearsAsCollection()
    {
        var workflowRunId = await SeedArtifactsAsync();
        var querier = CreateQuerier();

        var latest = await querier.ListLatestAsync(workflowRunId);

        var specs = Assert.Single(latest, a => a.Path == "specs/");
        Assert.Equal("directory", specs.Kind);
    }

    [Fact]
    public async Task Storage_ListDirectoryEntries_ReturnsContainedFiles()
    {
        var storage = CreateStorage();
        var storagePath = $"dir_{Guid.NewGuid():N}/files";

        await storage.WriteDirectoryAsync(
            storagePath,
            new List<WorkflowArtifactDirectoryEntryInput>
            {
                Entry("a.md", "# alpha content\n"),
                Entry("sub/b.md", "# beta content\n"),
            },
            new WorkflowArtifactFileWrite
            {
                SourcePath = "specs/",
                Size = 0,
                ContentType = "text/markdown",
            },
            BaseTime);

        var listing = await storage.ListDirectoryEntriesAsync(storagePath);

        var paths = listing.Entries.Select(e => e.RelativePath).ToList();
        Assert.Contains("a.md", paths);
        Assert.Contains("sub/b.md", paths);
    }

    [Fact]
    public async Task Storage_OpenDirectoryEntry_ReturnsRecordedBytes()
    {
        var storage = CreateStorage();
        var storagePath = $"dir_{Guid.NewGuid():N}/files";
        var alpha = "# alpha content\n";

        await storage.WriteDirectoryAsync(
            storagePath,
            new List<WorkflowArtifactDirectoryEntryInput> { Entry("a.md", alpha) },
            new WorkflowArtifactFileWrite
            {
                SourcePath = "specs/",
                Size = 0,
                ContentType = "text/markdown",
            },
            BaseTime);

        using var stream = storage.OpenDirectoryEntry(storagePath, "a.md");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        Assert.Equal(alpha, await reader.ReadToEndAsync());
    }

    private static WorkflowArtifactDirectoryEntryInput Entry(string relativePath, string content) => new()
    {
        RelativePath = relativePath,
        Size = Encoding.UTF8.GetByteCount(content),
        OpenContent = () => new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
    };
}
