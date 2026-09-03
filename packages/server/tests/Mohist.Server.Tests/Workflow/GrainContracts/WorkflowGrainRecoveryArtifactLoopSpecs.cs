using Mohist.Server.Runner.Grains;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.Tests.Workflow.GrainContracts;

/// <summary>
/// The review recovery loop against the real grain without a cluster: a
/// failed-expectation review injects its fix and retry tasks, and each
/// attempt produces an immutable review.md artifact version addressable by
/// task run, path history, latest query, and status summaries (#681).
/// </summary>
[Collection("MohistDb")]
[Trait("level", "L0")]
public sealed class WorkflowGrainRecoveryArtifactLoopSpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;
    private readonly InMemoryWorkflowArtifactStorage _storage = new();

    public WorkflowGrainRecoveryArtifactLoopSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RecoveryLoop_RunsFixThenRetriesReviewTask()
    {
        var captured = await RunRecoveryLoopOnceAsync("wr-loop-sequence");

        Assert.Equal(
            ["ai-review.1", "recover:fix-review-findings.1", "ai-review.2"],
            captured.WorkIds);
    }

    [Fact]
    public async Task RecoveryLoop_WorkflowActionAttemptFilterReturnsEachProducingReviewArtifact()
    {
        var captured = await RunRecoveryLoopOnceAsync("wr-loop-per-attempt");

        var querier = CreateArtifactQuerier();

        var firstRun = await querier.ListByWorkflowActionAttemptAsync(captured.WorkflowRunId, "ai-review.1");
        var secondRun = await querier.ListByWorkflowActionAttemptAsync(captured.WorkflowRunId, "ai-review.2");

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
        var captured = await RunRecoveryLoopOnceAsync("wr-loop-history");

        var history = await CreateArtifactQuerier().ListHistoryAsync(captured.WorkflowRunId, "review.md");

        Assert.Equal(["ai-review.1", "ai-review.2"], history.Select(a => a.ActionAttemptId).ToArray());
    }

    [Fact]
    public async Task RecoveryLoop_LatestQueryReturnsRetriedReview()
    {
        var captured = await RunRecoveryLoopOnceAsync("wr-loop-latest");

        var latest = await CreateArtifactQuerier().ListLatestAsync(captured.WorkflowRunId);

        var review = Assert.Single(latest);
        Assert.Equal("review.md", review.Path);
        Assert.Equal("ai-review.2", review.ActionAttemptId);
        Assert.Equal("review-round-2: PASS", ReadStorageContent(review.ArtifactStoragePath));
    }

    [Fact]
    public async Task RecoveryLoop_WorkflowActionAttemptViewsExposeBothImmutableArtifacts()
    {
        var captured = await RunRecoveryLoopOnceAsync("wr-loop-summaries");

        var status = await captured.Arrangement.Querier.GetStatusAsync(captured.WorkflowRunId);
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

    private async Task<RecoveryLoopRun> RunRecoveryLoopOnceAsync(string runId)
    {
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture,
            runId,
            RecoveryLoopDefinition(),
            TimeProvider,
            workerId: $"runner-{runId}");

        var review1 = (await arrangement.AssignAndClaimAsync())!;
        Assert.Equal("ai-review.1", review1.Id);
        Assert.NotNull(review1.Recovery);

        var firstUploadId = await SeedReviewPendingUploadAsync(
            arrangement, review1.Id!, "review-round-1: FAIL");
        await arrangement.ReportTaskResultAsync(
            review1,
            output: JsonSerializer.SerializeToElement(new { promise = "FAIL" }),
            addTasks:
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
                    Artifacts: ReviewArtifacts()),
            ],
            artifactUploadIds: [firstUploadId]);

        var fix = (await arrangement.AssignAndClaimAsync())!;
        Assert.Equal("recover:fix-review-findings.1", fix.Id);
        await arrangement.ReportCompletedAsync(fix);

        var review2 = (await arrangement.AssignAndClaimAsync())!;
        Assert.Equal("ai-review.2", review2.Id);

        var secondUploadId = await SeedReviewPendingUploadAsync(
            arrangement, review2.Id!, "review-round-2: PASS");
        await arrangement.ReportTaskResultAsync(
            review2,
            output: JsonSerializer.SerializeToElement(new { promise = "PASS" }),
            addTasks: null,
            artifactUploadIds: [secondUploadId]);

        return new RecoveryLoopRun(arrangement, [review1.Id!, fix.Id!, review2.Id!]);
    }

    private WorkflowArtifactQuerier CreateArtifactQuerier()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        return new WorkflowArtifactQuerier(new PooledDbContextFactory<MohistDbContext>(options));
    }

    private async Task<string> SeedReviewPendingUploadAsync(
        WorkflowGrainArrangement arrangement, string actionAttemptId, string content)
    {
        var uploadId = $"artup_{Guid.NewGuid():N}";
        var storagePath = _storage.GenerateStoragePath(
            arrangement.RunId, actionAttemptId, uploadId, WorkflowArtifactStorageKind.File);

        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await _storage.WriteFileAsync(
            storagePath,
            new MemoryStream(bytes, writable: false),
            new WorkflowArtifactFileWrite
            {
                SourcePath = "review.md",
                Size = bytes.Length,
                ContentType = "text/markdown",
                ContentHash = $"sha256:{Guid.NewGuid():N}",
            },
            TimeProvider.GetUtcNow());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
        {
            UploadId = uploadId,
            WorkflowRunId = arrangement.RunId,
            WorkId = $"ai-review.{actionAttemptId.Split('.')[^1]}",
            ActionAttemptId = actionAttemptId,
            Path = "review.md",
            ContentType = "text/markdown",
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Size = bytes.Length,
            StoragePath = storagePath,
            CreatedAt = TimeProvider.GetUtcNow(),
            ExpiresAt = TimeProvider.GetUtcNow().AddDays(1),
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

    private static RecoveryDefinition ReviewRecovery() =>
        new(
            1,
            [
                new RecoveryHandlerDefinition(
                    "output.promise=FAIL",
                    [
                        new TaskDefinition(
                            "recover:fix-review-findings",
                            "Fix review findings",
                            "spec/fix-review")
                    ],
                    RetrySelf: true)
            ]);

    private static Dictionary<string, JsonElement?> ReviewWith() => JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
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
        """)!;

    private static TaskArtifactCapture ReviewArtifacts() =>
        new([new TaskArtifactDeclaration("review.md")]);

    private static WorkflowDefinition RecoveryLoopDefinition() =>
        new([
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

    private sealed record RecoveryLoopRun(WorkflowGrainArrangement Arrangement, IReadOnlyList<string> WorkIds)
    {
        public string WorkflowRunId => Arrangement.RunId;
    }
}
