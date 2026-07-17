using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowGrain")]
public class WorkflowCheckLoopArtifactSpecs : WorkflowGrainSpecs
{
    private readonly InMemoryWorkflowArtifactStorage _storage = new();

    public WorkflowCheckLoopArtifactSpecs(WorkflowGrainFixture fixture) : base(fixture)
    {
    }

    private static RecoveryDefinition ReviewRecovery() =>
        new(
            1,
            [
                new RecoveryHandlerDefinition(
                    "promise=FAIL",
                    [
                        new TaskDefinition(
                            "recover:fix-review-findings",
                            "Fix review findings",
                            "spec/fix-review")
                    ],
                    RetrySelf: true)
            ]);

    private static Dictionary<string, JsonElement?> ReviewWith() => With("""
        {
          "expect": {
            "markers": [
              {
                "path": "review.md",
                "oneOf": ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
                "failIf": "<promise>FAIL</promise>"
              }
            ]
          }
        }
        """);

    private static TaskArtifactCapture ReviewArtifacts() =>
        new([new TaskArtifactDeclaration("review.md")]);

    private static WorkflowDefinition RecoveryLoopDefinition() =>
        new("spec/workflow", [
            new StageDefinition("check",
                [
                    new TaskDefinition(
                        "ai-review",
                        "AI review",
                        "spec/review",
                        ReviewWith(),
                        Expect: null,
                        Artifacts: ReviewArtifacts(),
                        Recovery: ReviewRecovery())
                ],
                [])
        ]);

    [Fact]
    public async Task RecoveryLoop_RunsFixThenRetriesReviewTask()
    {
        var captured = await RunRecoveryLoopOnceAsync();

        Assert.Equal(
            ["ai-review.1", "recover:fix-review-findings.1", "ai-review.2"],
            captured.WorkIds);
    }

    [Fact]
    public async Task RecoveryLoop_TaskRunFilterReturnsEachProducingReviewArtifact()
    {
        var captured = await RunRecoveryLoopOnceAsync();

        var scopedFactory = CreateDbContextFactory();
        var realQuerier = new WorkflowArtifactQuerier(scopedFactory);

        var firstRun = await realQuerier.ListByTaskRunAsync(captured.WorkflowRunId, "ai-review.1");
        var secondRun = await realQuerier.ListByTaskRunAsync(captured.WorkflowRunId, "ai-review.2");

        var firstReview = Assert.Single(firstRun);
        Assert.Equal("review.md", firstReview.Path);
        Assert.Contains("FAIL", ReadStorageContent(firstReview.ArtifactStoragePath));

        var secondReview = Assert.Single(secondRun);
        Assert.Equal("review.md", secondReview.Path);
        Assert.Contains("PASS", ReadStorageContent(secondReview.ArtifactStoragePath));
    }

    [Fact]
    public async Task RecoveryLoop_PathHistoryReturnsBothReviewVersions()
    {
        var captured = await RunRecoveryLoopOnceAsync();

        var scopedFactory = CreateDbContextFactory();
        var realQuerier = new WorkflowArtifactQuerier(scopedFactory);

        var history = await realQuerier.ListHistoryAsync(captured.WorkflowRunId, "review.md");

        Assert.Equal(["ai-review.1", "ai-review.2"], history.Select(a => a.TaskRunId).ToArray());
    }

    [Fact]
    public async Task RecoveryLoop_LatestQueryReturnsRetriedReview()
    {
        var captured = await RunRecoveryLoopOnceAsync();

        var scopedFactory = CreateDbContextFactory();
        var realQuerier = new WorkflowArtifactQuerier(scopedFactory);

        var latest = await realQuerier.ListLatestAsync(captured.WorkflowRunId);

        var review = Assert.Single(latest);
        Assert.Equal("review.md", review.Path);
        Assert.Equal("ai-review.2", review.TaskRunId);
        Assert.Equal("review-round-2: PASS", ReadStorageContent(review.ArtifactStoragePath));
    }

    [Fact]
    public async Task RecoveryLoop_TaskRunViewsExposeBothImmutableArtifacts()
    {
        var captured = await RunRecoveryLoopOnceAsync();

        var status = await GetQuerier().GetStatusAsync(captured.WorkflowRunId);
        Assert.NotNull(status);

        var checkStage = Assert.Single(status!.Stages);
        var reviewTasks = checkStage.Tasks.Where(t => t.Id.StartsWith("ai-review.", StringComparison.Ordinal)).ToList();
        Assert.Equal(["ai-review.1", "ai-review.2"], reviewTasks.Select(t => t.Id).ToArray());

        foreach (var task in reviewTasks)
        {
            Assert.NotNull(task.ArtifactSummaries);
            var summary = Assert.Single(task.ArtifactSummaries!);
            Assert.Equal("review.md", summary.Path);
            Assert.Equal("file", summary.Kind);
        }
    }

    private async Task<RecoveryLoopRun> RunRecoveryLoopOnceAsync()
    {
        await StartWorkflowAsync(RecoveryLoopDefinition());
        var workIds = new List<string>();

        var (review1, r1) = await PollWorkAnyAsync();
        Assert.Equal("ai-review.1", review1.WorkId);
        Assert.NotNull(review1.Recovery);
        workIds.Add(review1.WorkId);

        var firstUploadId = await SeedReviewPendingUploadAsync(
            review1.WorkflowRunId, review1.WorkId, "ai-review.1", "review.md",
            "review-round-1: FAIL");

        await ReportAsync(r1, review1.WorkId, new WorkResult(
            "completed",
            Output: """{"promise":"FAIL"}""",
            ArtifactUploadIds: [firstUploadId],
            AddTasks:
            [
                new RuntimeTaskInput(
                    "recover:fix-review-findings",
                    "Fix review findings",
                    "spec/fix-review"),
                new RuntimeTaskInput(
                    "ai-review",
                    "AI review",
                    "spec/review",
                    With: JsonSerializer.SerializeToElement(ReviewWith(), WorkflowYamlSerializer.JsonOptions),
                    Recovery: ReviewRecovery(),
                    RecoveryRemaining: 0,
                    Artifacts: ReviewArtifacts())
            ]));

        var (fix, r2) = await PollWorkAnyAsync();
        Assert.Equal("recover:fix-review-findings.1", fix.WorkId);
        workIds.Add(fix.WorkId);
        await ReportAsync(r2, fix.WorkId, "completed");

        var (review2, r3) = await PollWorkAnyAsync();
        Assert.Equal("ai-review.2", review2.WorkId);
        workIds.Add(review2.WorkId);

        var secondUploadId = await SeedReviewPendingUploadAsync(
            review2.WorkflowRunId, review2.WorkId, "ai-review.2", "review.md",
            "review-round-2: PASS");
        await ReportAsync(r3, review2.WorkId, new WorkResult(
            "completed",
            Output: """{"promise":"PASS"}""",
            ArtifactUploadIds: [secondUploadId]));

        return new RecoveryLoopRun(review1.WorkflowRunId, workIds);
    }

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
            TestTime.UtcNow);

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
            CreatedAt = TestTime.UtcNow,
            ExpiresAt = TestTime.UtcNow.AddDays(1),
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

    private sealed record RecoveryLoopRun(string WorkflowRunId, IReadOnlyList<string> WorkIds);
}
