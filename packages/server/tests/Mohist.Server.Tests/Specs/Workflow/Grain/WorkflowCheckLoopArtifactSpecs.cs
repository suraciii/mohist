using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

/// <summary>
/// Cross-layer coverage for the check-loop preservation promise from
/// issue #55: a workflow's repair loop runs <c>ai-review</c> more than
/// once, every <c>review.md</c> stays addressable, and the recorded
/// content survives even after the workspace rewrites the same path.
/// </summary>
public class WorkflowCheckLoopArtifactSpecs : WorkflowGrainSpecs, IDisposable
{
    private readonly string _storageRoot;
    private readonly FileSystemWorkflowArtifactStorage _storage;

    public WorkflowCheckLoopArtifactSpecs(WorkflowGrainFixture fixture) : base(fixture)
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), $"mohist-check-loop-{Guid.NewGuid():N}");
        _storage = new FileSystemWorkflowArtifactStorage(
            _storageRoot,
            NullLogger<FileSystemWorkflowArtifactStorage>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_storageRoot))
                Directory.Delete(_storageRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static WorkflowDefinition CheckLoopDefinition() =>
        new("spec/workflow", [
            new StageDefinition("check",
                [new("ai-review", "AI review", "spec/review",
                    Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]))],
                [new("review-passed", "Review passed", "spec/marker",
                    OnFailure: new CheckFailureAction(new CheckFailureRepair(
                        2,
                        new TaskDefinition("fix-review-findings", "Fix review findings", "spec/fix-review"),
                        new TaskDefinition("ai-review", "AI review", "spec/review"))))])
        ]);

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckLoop_RecordsEachReviewVersionAsSeparateImmutableArtifact()
    {
        await StartWorkflowAsync(CheckLoopDefinition());

        // First ai-review.1 produces a failing review.
        var (review1, r1) = await PollWorkAnyAsync();
        Assert.Equal("ai-review.1", review1.WorkId);
        var firstUploadId = await SeedReviewPendingUploadAsync(
            review1.WorkflowRunId, review1.WorkId, "ai-review.1", "review.md",
            "review-round-1: FAIL");
        await ReportAsync(r1, review1.WorkId, new WorkResult("completed", ArtifactUploadIds: [firstUploadId]));

        // First check fails -> repair.
        var (checks1, r2) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks1.WorkType);
        await ReportChecksFailAsync(r2, checks1, "review-passed", "marker missing");

        // fix-review-findings runs and completes.
        var (fix, r3) = await PollWorkAnyAsync();
        Assert.Equal("fix-review-findings:1.1", fix.WorkId);
        await ReportAsync(r3, fix.WorkId, "completed");

        // Second ai-review.2 produces a passing review.
        var (review2, r4) = await PollWorkAnyAsync();
        Assert.Equal("ai-review.2", review2.WorkId);
        var secondUploadId = await SeedReviewPendingUploadAsync(
            review2.WorkflowRunId, review2.WorkId, "ai-review.2", "review.md",
            "review-round-2: PASS");
        await ReportAsync(r4, review2.WorkId, new WorkResult("completed", ArtifactUploadIds: [secondUploadId]));

        // Second check passes.
        var (checks2, r5) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks2.WorkType);
        Assert.NotEqual(checks1.WorkId, checks2.WorkId);
        await ReportChecksPassAsync(r5, checks2, "review-passed");

        var workflowRunId = review1.WorkflowRunId;
        await using var db = CreateDb();
        var reviewRows = (await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == workflowRunId && a.Path == "review.md")
            .ToListAsync())
            .OrderBy(a => a.RecordedAt)
            .ToList();

        // Both ai-review task runs produced an immutable review.md record.
        Assert.Equal(2, reviewRows.Count);
        Assert.Equal("ai-review.1", reviewRows[0].TaskRunId);
        Assert.Equal("ai-review.2", reviewRows[1].TaskRunId);
        Assert.NotEqual(reviewRows[0].ArtifactId, reviewRows[1].ArtifactId);
        Assert.NotEqual(reviewRows[0].ArtifactStoragePath, reviewRows[1].ArtifactStoragePath);
        Assert.True(reviewRows[1].RecordedAt >= reviewRows[0].RecordedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckLoop_TaskRunFilterReturnsProducingReviewArtifact()
    {
        var workflowRunId = await RunCheckLoopOnceAsync();

        // Use the querier the public API surface composes so the
        // assertion matches what the issue-scoped query endpoint returns.
        var scopedFactory = CreateDbContextFactory();
        var realQuerier = new WorkflowArtifactQuerier(scopedFactory);

        var firstRun = await realQuerier.ListByTaskRunAsync(workflowRunId, "ai-review.1");
        var secondRun = await realQuerier.ListByTaskRunAsync(workflowRunId, "ai-review.2");

        var firstReview = Assert.Single(firstRun);
        Assert.Equal("review.md", firstReview.Path);
        Assert.Equal("ai-review.1", firstReview.TaskRunId);
        Assert.Contains("FAIL", ReadStorageContent(firstReview.ArtifactStoragePath));

        var secondReview = Assert.Single(secondRun);
        Assert.Equal("review.md", secondReview.Path);
        Assert.Equal("ai-review.2", secondReview.TaskRunId);
        Assert.Contains("PASS", ReadStorageContent(secondReview.ArtifactStoragePath));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckLoop_PathHistoryReturnsBothVersionsInProductionOrder()
    {
        var workflowRunId = await RunCheckLoopOnceAsync();

        var scopedFactory = CreateDbContextFactory();
        var realQuerier = new WorkflowArtifactQuerier(scopedFactory);

        var history = await realQuerier.ListHistoryAsync(workflowRunId, "review.md");

        Assert.Equal(2, history.Count);
        Assert.Equal("ai-review.1", history[0].TaskRunId);
        Assert.Equal("ai-review.2", history[1].TaskRunId);
        Assert.True(history[1].RecordedAt >= history[0].RecordedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckLoop_LatestQueryReturnsOnlyNewestReview()
    {
        var workflowRunId = await RunCheckLoopOnceAsync();

        var scopedFactory = CreateDbContextFactory();
        var realQuerier = new WorkflowArtifactQuerier(scopedFactory);

        var latest = await realQuerier.ListLatestAsync(workflowRunId);

        var review = Assert.Single(latest);
        Assert.Equal("review.md", review.Path);
        Assert.Equal("ai-review.2", review.TaskRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckLoop_RecordedContentReadsReturnStoredHistoricalContent()
    {
        var workflowRunId = await RunCheckLoopOnceAsync();

        var scopedFactory = CreateDbContextFactory();
        var realQuerier = new WorkflowArtifactQuerier(scopedFactory);

        var history = await realQuerier.ListHistoryAsync(workflowRunId, "review.md");
        var v1 = history.Single(a => a.TaskRunId == "ai-review.1");
        var v2 = history.Single(a => a.TaskRunId == "ai-review.2");

        // The recorded content for each version must be the bytes the
        // runner wrote during that task run — not anything that may
        // exist in the live workspace afterwards. This is the
        // historical guarantee the user pain point depends on.
        var v1Bytes = ReadStorageContent(v1.ArtifactStoragePath);
        var v2Bytes = ReadStorageContent(v2.ArtifactStoragePath);

        Assert.Equal("review-round-1: FAIL", v1Bytes);
        Assert.Equal("review-round-2: PASS", v2Bytes);
        Assert.NotEqual(v1Bytes, v2Bytes);

        // Even if the storage path's filename is the same, opening
        // each version returns its own immutable content. Overwriting
        // the workspace file does not change recorded bytes.
        var v1Again = ReadStorageContent(v1.ArtifactStoragePath);
        Assert.Equal(v1Bytes, v1Again);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckLoop_TaskRunViewsExposeOwnImmutableArtifact()
    {
        var workflowRunId = await RunCheckLoopOnceAsync();

        // The workflow history read model should expose each ai-review
        // task run with the artifact summary it produced. This is the
        // second complementary view promised in the issue ("Task
        // artifacts" view on the issue page).
        var status = await GetQuerier().GetStatusAsync(workflowRunId);
        Assert.NotNull(status);

        var checkStage = Assert.Single(status!.Stages);
        var reviewTasks = checkStage.Tasks.Where(t => t.Id.StartsWith("ai-review.")).ToList();
        Assert.Equal(2, reviewTasks.Count);

        var v1Task = reviewTasks.Single(t => t.Id == "ai-review.1");
        var v2Task = reviewTasks.Single(t => t.Id == "ai-review.2");

        Assert.NotNull(v1Task.ArtifactSummaries);
        var v1Summary = Assert.Single(v1Task.ArtifactSummaries!);
        Assert.Equal("review.md", v1Summary.Path);
        Assert.Equal("file", v1Summary.Kind);

        Assert.NotNull(v2Task.ArtifactSummaries);
        var v2Summary = Assert.Single(v2Task.ArtifactSummaries!);
        Assert.Equal("review.md", v2Summary.Path);
        Assert.Equal("file", v2Summary.Kind);

        // The two task rows must point at different artifact ids;
        // the later version must not overwrite or shadow the earlier
        // one from the user's perspective.
        Assert.NotEqual(v1Summary.ArtifactId, v2Summary.ArtifactId);
    }

    /// <summary>
    /// Drives the full ai-review → check fail → fix → ai-review.2 →
    /// check pass flow once and returns the workflow run id. The
    /// first review is "FAIL", the second is "PASS", each backed by
    /// a separate uploaded <c>review.md</c> file on the artifact
    /// storage root.
    /// </summary>
    private async Task<string> RunCheckLoopOnceAsync()
    {
        await StartWorkflowAsync(CheckLoopDefinition());

        var (review1, r1) = await PollWorkAnyAsync();
        Assert.Equal("ai-review.1", review1.WorkId);
        var firstUploadId = await SeedReviewPendingUploadAsync(
            review1.WorkflowRunId, review1.WorkId, "ai-review.1", "review.md",
            "review-round-1: FAIL");
        await ReportAsync(r1, review1.WorkId, new WorkResult("completed", ArtifactUploadIds: [firstUploadId]));

        var (checks1, r2) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks1.WorkType);
        await ReportChecksFailAsync(r2, checks1, "review-passed", "marker missing");

        var (fix, r3) = await PollWorkAnyAsync();
        Assert.Equal("fix-review-findings:1.1", fix.WorkId);
        await ReportAsync(r3, fix.WorkId, "completed");

        var (review2, r4) = await PollWorkAnyAsync();
        Assert.Equal("ai-review.2", review2.WorkId);
        var secondUploadId = await SeedReviewPendingUploadAsync(
            review2.WorkflowRunId, review2.WorkId, "ai-review.2", "review.md",
            "review-round-2: PASS");
        await ReportAsync(r4, review2.WorkId, new WorkResult("completed", ArtifactUploadIds: [secondUploadId]));

        var (checks2, r5) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks2.WorkType);
        await ReportChecksPassAsync(r5, checks2, "review-passed");

        return review1.WorkflowRunId;
    }

    /// <summary>
    /// Seeds a pending upload row whose content is written through the
    /// real artifact storage. Mirrors what the runner upload endpoint
    /// does end-to-end, so the bound <c>WorkflowArtifact</c> row has
    /// a valid <c>ArtifactStoragePath</c> pointing at real bytes.
    /// </summary>
    private async Task<string> SeedReviewPendingUploadAsync(
        string workflowRunId,
        string workId,
        string taskRunId,
        string path,
        string content)
    {
        var uploadId = $"artup_{Guid.NewGuid():N}";
        var storagePath = _storage.GenerateStoragePath(
            workflowRunId, taskRunId, uploadId, WorkflowArtifactStorageKind.File);

        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await _storage.WriteFileAsync(
            storagePath,
            new MemoryStream(bytes, writable: false),
            new WorkflowArtifactFileWrite
            {
                SourcePath = path,
                Size = bytes.Length,
                ContentType = "text/markdown",
                ContentHash = $"sha256:{Guid.NewGuid():N}",
            },
            DateTimeOffset.UtcNow);

        await using var db = CreateDb();
        db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
        {
            UploadId = uploadId,
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            TaskRunId = taskRunId,
            Path = path,
            ContentType = "text/markdown",
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Size = bytes.Length,
            StoragePath = storagePath,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();
        return uploadId;
    }

    private string ReadStorageContent(string storagePath)
    {
        using var stream = _storage.OpenFileContent(storagePath);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private MohistDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        return new MohistDbContext(options);
    }

    private IDbContextFactory<MohistDbContext> CreateDbContextFactory()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        return new PooledDbContextFactory<MohistDbContext>(options);
    }
}
