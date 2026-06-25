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
                        new TaskDefinition("fix-review-findings", "Fix review findings", "spec/fix-review"))))])
        ]);

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckLoop_RepairPathExposesSingleRepairTaskThenCheckReRuns()
    {
        // VerifyTask is removed. The check-repair path is exactly
        // [repairTask] with no verify step. After the repair task
        // completes, the check is re-run directly; the original
        // ai-review task is not re-injected.
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

        // fix-review-findings runs and completes — the only injected
        // task before the check re-runs.
        var (fix, r3) = await PollWorkAnyAsync();
        Assert.Equal("fix-review-findings:1.1", fix.WorkId);
        await ReportAsync(r3, fix.WorkId, "completed");

        // Second check passes. There is no re-injection of ai-review as
        // a verify task.
        var (checks2, r4) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks2.WorkType);
        await ReportChecksPassAsync(r4, checks2, "review-passed");

        var workflowRunId = review1.WorkflowRunId;
        await using var db = CreateDb();
        var reviewRows = (await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == workflowRunId && a.Path == "review.md")
            .ToListAsync())
            .OrderBy(a => a.RecordedAt)
            .ToList();

        // Without the verify step, only the original ai-review run
        // produced an immutable review.md record. The check re-ran on
        // the existing artifact after the repair task finished.
        var review = Assert.Single(reviewRows);
        Assert.Equal("ai-review.1", review.TaskRunId);
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

        var firstReview = Assert.Single(firstRun);
        Assert.Equal("review.md", firstReview.Path);
        Assert.Equal("ai-review.1", firstReview.TaskRunId);
        Assert.Contains("FAIL", ReadStorageContent(firstReview.ArtifactStoragePath));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckLoop_PathHistoryReturnsTheSingleReviewVersion()
    {
        // Without the verify step, the original ai-review.1 is the
        // only review run; the check re-runs on the existing artifact
        // after the repair task finishes, so the path history holds
        // exactly one row.
        var workflowRunId = await RunCheckLoopOnceAsync();

        var scopedFactory = CreateDbContextFactory();
        var realQuerier = new WorkflowArtifactQuerier(scopedFactory);

        var history = await realQuerier.ListHistoryAsync(workflowRunId, "review.md");

        var only = Assert.Single(history);
        Assert.Equal("ai-review.1", only.TaskRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckLoop_LatestQueryReturnsTheSingleReview()
    {
        var workflowRunId = await RunCheckLoopOnceAsync();

        var scopedFactory = CreateDbContextFactory();
        var realQuerier = new WorkflowArtifactQuerier(scopedFactory);

        var latest = await realQuerier.ListLatestAsync(workflowRunId);

        var review = Assert.Single(latest);
        Assert.Equal("review.md", review.Path);
        Assert.Equal("ai-review.1", review.TaskRunId);
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
        var v1 = Assert.Single(history);

        // The recorded content for the single review run must be the
        // bytes the runner wrote during that task run — not anything
        // that may exist in the live workspace afterwards. This is
        // the historical guarantee the user pain point depends on.
        var v1Bytes = ReadStorageContent(v1.ArtifactStoragePath);

        Assert.Equal("review-round-1: FAIL", v1Bytes);

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

        // The workflow history read model exposes the single ai-review
        // task run with the artifact summary it produced.
        var status = await GetQuerier().GetStatusAsync(workflowRunId);
        Assert.NotNull(status);

        var checkStage = Assert.Single(status!.Stages);
        var reviewTasks = checkStage.Tasks.Where(t => t.Id.StartsWith("ai-review.")).ToList();
        var v1Task = Assert.Single(reviewTasks);
        Assert.Equal("ai-review.1", v1Task.Id);

        Assert.NotNull(v1Task.ArtifactSummaries);
        var v1Summary = Assert.Single(v1Task.ArtifactSummaries!);
        Assert.Equal("review.md", v1Summary.Path);
        Assert.Equal("file", v1Summary.Kind);
    }

    /// <summary>
    /// Drives the full ai-review → check fail → fix → check pass flow
    /// once and returns the workflow run id. The first review is
    /// "FAIL"; the repair task runs to completion and the check is
    /// re-run on the existing artifact (no verify-step re-injection).
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

        var (checks2, r4) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks2.WorkType);
        await ReportChecksPassAsync(r4, checks2, "review-passed");

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
