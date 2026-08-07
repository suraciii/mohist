using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Artifacts;

[Collection("MohistDb")]
public class WorkflowArtifactPersistenceSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    private readonly MohistDbFixture _fixture;

    public WorkflowArtifactPersistenceSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private (string workflowRunId, string taskRunId, string prefix) NewIds(string label) =>
        ($"wr_{label}_{Guid.NewGuid():N}",
         $"{label}.{Guid.NewGuid():N}",
         $"{label}_{Guid.NewGuid():N}_");

    [Fact]
    public async Task WorkflowArtifactRow_RoundTripsCoreFields()
    {
        var (wr, tr, prefix) = NewIds("core");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var recordedAt = new DateTimeOffset(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

        var row = new WorkflowArtifactRow
        {
            ArtifactId = $"art_{prefix}",
            WorkflowRunId = wr,
            TaskRunId = tr,
            Path = "review.md",
            RecordedAt = recordedAt,
            ArtifactStoragePath = $"{wr}/tasks/{tr}/artifacts/art_{prefix}/content",
            Kind = "file",
            ContentType = "text/markdown",
            ContentHash = "sha256:abc",
            Size = 1024,
            ProjectId = "proj-1",
            IssueNumber = 1,
            DisplayName = "review.md",
        };

        db.WorkflowArtifacts.Add(row);
        await db.SaveChangesAsync();

        var reloaded = await db.WorkflowArtifacts.SingleAsync(a => a.ArtifactId == row.ArtifactId);
        Assert.Equal(wr, reloaded.WorkflowRunId);
        Assert.Equal(tr, reloaded.TaskRunId);
        Assert.Equal("review.md", reloaded.Path);
        Assert.Equal(recordedAt, reloaded.RecordedAt);
        Assert.Equal($"{wr}/tasks/{tr}/artifacts/art_{prefix}/content", reloaded.ArtifactStoragePath);
        Assert.Equal("file", reloaded.Kind);
        Assert.Equal("text/markdown", reloaded.ContentType);
        Assert.Equal("sha256:abc", reloaded.ContentHash);
        Assert.Equal(1024L, reloaded.Size);
    }

    [Fact]
    public async Task WorkflowArtifactRow_PersistsDirectoryKind()
    {
        var (wr, tr, prefix) = NewIds("dir");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var artifactId = $"art_{prefix}";
        db.WorkflowArtifacts.Add(new WorkflowArtifactRow
        {
            ArtifactId = artifactId,
            WorkflowRunId = wr,
            TaskRunId = tr,
            Path = "specs/",
            RecordedAt = FixedNow,
            ArtifactStoragePath = $"{wr}/tasks/{tr}/artifacts/{artifactId}/files",
            Kind = "directory",
        });
        await db.SaveChangesAsync();

        var reloaded = await db.WorkflowArtifacts.SingleAsync(a => a.ArtifactId == artifactId);
        Assert.Equal("directory", reloaded.Kind);
    }

    [Fact]
    public async Task WorkflowArtifactRow_LatestProjectionByPathIsPossible()
    {
        // The acceptance criteria require that the bound rows support
        // a latest-per-path query inside a workflow run. We assert
        // that the (WorkflowRunId, Path, RecordedAt) index can be
        // used to drive that projection.
        var (wr, _, _) = NewIds("latest");
        var otherWr = $"wr_other_{Guid.NewGuid():N}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var baseTime = new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero);

        db.WorkflowArtifacts.AddRange(
            new WorkflowArtifactRow
            {
                ArtifactId = $"art_v1_{Guid.NewGuid():N}",
                WorkflowRunId = wr,
                TaskRunId = "ai-review.1",
                Path = "review.md",
                RecordedAt = baseTime,
                ArtifactStoragePath = "v1",
            },
            new WorkflowArtifactRow
            {
                ArtifactId = $"art_v2_{Guid.NewGuid():N}",
                WorkflowRunId = wr,
                TaskRunId = "ai-review.2",
                Path = "review.md",
                RecordedAt = baseTime.AddMinutes(5),
                ArtifactStoragePath = "v2",
            },
            new WorkflowArtifactRow
            {
                ArtifactId = $"art_v3_other_{Guid.NewGuid():N}",
                WorkflowRunId = otherWr,
                TaskRunId = "ai-review.1",
                Path = "review.md",
                RecordedAt = baseTime.AddMinutes(10),
                ArtifactStoragePath = "v3",
            });
        await db.SaveChangesAsync();

        // SQLite cannot ORDER BY DateTimeOffset directly; resolve to
        // a list and order on the client. The indexed columns still
        // narrow the result set on the server.
        var rowsForRun = await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == wr && a.Path == "review.md")
            .ToListAsync();
        var latestForRun = rowsForRun
            .OrderByDescending(a => a.RecordedAt)
            .First();

        Assert.Equal("ai-review.2", latestForRun.TaskRunId);
        Assert.Equal(baseTime.AddMinutes(5), latestForRun.RecordedAt);
    }

    [Fact]
    public async Task WorkflowArtifactRow_HistoryByPathReturnsAllVersionsInProductionOrder()
    {
        var (wr, _, _) = NewIds("hist");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var baseTime = new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero);
        var v1Id = $"art_h1_{Guid.NewGuid():N}";
        var v2Id = $"art_h2_{Guid.NewGuid():N}";
        var v3Id = $"art_h3_{Guid.NewGuid():N}";

        db.WorkflowArtifacts.AddRange(
            new WorkflowArtifactRow
            {
                ArtifactId = v1Id,
                WorkflowRunId = wr,
                TaskRunId = "ai-review.1",
                Path = "review.md",
                RecordedAt = baseTime,
                ArtifactStoragePath = "h1",
            },
            new WorkflowArtifactRow
            {
                ArtifactId = v2Id,
                WorkflowRunId = wr,
                TaskRunId = "ai-review.2",
                Path = "review.md",
                RecordedAt = baseTime.AddMinutes(3),
                ArtifactStoragePath = "h2",
            },
            new WorkflowArtifactRow
            {
                ArtifactId = v3Id,
                WorkflowRunId = wr,
                TaskRunId = "ai-review.3",
                Path = "review.md",
                RecordedAt = baseTime.AddMinutes(6),
                ArtifactStoragePath = "h3",
            });
        await db.SaveChangesAsync();

        var history = (await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == wr && a.Path == "review.md")
            .ToListAsync())
            .OrderBy(a => a.RecordedAt)
            .Select(a => a.ArtifactId)
            .ToList();

        Assert.Equal(new[] { v1Id, v2Id, v3Id }, history);
    }

    [Fact]
    public async Task WorkflowArtifactRow_TaskRunFilterReturnsProducedArtifacts()
    {
        var (wr, _, _) = NewIds("filter");
        var firstTask = $"ai-review.1_{Guid.NewGuid():N}";
        var secondTask = $"ai-review.2_{Guid.NewGuid():N}";
        var firstId = $"art_a1_{Guid.NewGuid():N}";
        var secondId = $"art_a2_{Guid.NewGuid():N}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        db.WorkflowArtifacts.AddRange(
            new WorkflowArtifactRow
            {
                ArtifactId = firstId,
                WorkflowRunId = wr,
                TaskRunId = firstTask,
                Path = "review.md",
                RecordedAt = FixedNow,
                ArtifactStoragePath = "a1",
            },
            new WorkflowArtifactRow
            {
                ArtifactId = secondId,
                WorkflowRunId = wr,
                TaskRunId = secondTask,
                Path = "review.md",
                RecordedAt = FixedNow,
                ArtifactStoragePath = "a2",
            });
        await db.SaveChangesAsync();

        var firstRunArtifacts = await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == wr && a.TaskRunId == firstTask)
            .ToListAsync();

        var single = Assert.Single(firstRunArtifacts);
        Assert.Equal(firstId, single.ArtifactId);
    }

    [Fact]
    public async Task WorkflowArtifactPendingUpload_RoundTripsIdempotencyFields()
    {
        var (wr, tr, _) = NewIds("pending");
        var workId = $"work_{Guid.NewGuid():N}";
        var uploadId = $"artup_{Guid.NewGuid():N}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var now = FixedNow;

        db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
        {
            UploadId = uploadId,
            WorkflowRunId = wr,
            WorkId = workId,
            TaskRunId = tr,
            Path = "review.md",
            ContentType = "text/markdown",
            ContentHash = "sha256:def",
            Size = 2048,
            StoragePath = $"pending/{wr}/{workId}/{tr}/review.md",
            CreatedAt = now,
            ExpiresAt = now.AddHours(1),
        });
        await db.SaveChangesAsync();

        var reloaded = await db.WorkflowArtifactPendingUploads.SingleAsync(u => u.UploadId == uploadId);
        Assert.Equal(wr, reloaded.WorkflowRunId);
        Assert.Equal(workId, reloaded.WorkId);
        Assert.Equal(tr, reloaded.TaskRunId);
        Assert.Equal("review.md", reloaded.Path);
        Assert.Equal("sha256:def", reloaded.ContentHash);
        Assert.Equal(2048L, reloaded.Size);
    }

    [Fact]
    public async Task WorkflowArtifactPendingUpload_IdempotencyKeyRejectsDuplicate()
    {
        var (wr, tr, _) = NewIds("dup");
        var workId = $"work_{Guid.NewGuid():N}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var now = FixedNow;

        db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
        {
            UploadId = $"artup_dup_1_{Guid.NewGuid():N}",
            WorkflowRunId = wr,
            WorkId = workId,
            TaskRunId = tr,
            Path = "review.md",
            StoragePath = "p1",
            CreatedAt = now,
            ExpiresAt = now.AddHours(1),
        });
        await db.SaveChangesAsync();

        db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
        {
            UploadId = $"artup_dup_2_{Guid.NewGuid():N}",
            WorkflowRunId = wr,
            WorkId = workId,
            TaskRunId = tr,
            Path = "review.md",
            StoragePath = "p2",
            CreatedAt = now,
            ExpiresAt = now.AddHours(1),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void EfModelSnapshot_ExposesIndexesForLatestHistoryAndTaskRunQueries()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var artifactEntity = db.Model.FindEntityType(typeof(WorkflowArtifactRow))!;
        var indexNames = artifactEntity.GetIndexes()
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("WorkflowRunId,Path,RecordedAt", indexNames);
        Assert.Contains("WorkflowRunId,TaskRunId,RecordedAt", indexNames);
        Assert.Contains("ProjectId,IssueNumber,RecordedAt", indexNames);
    }

    [Fact]
    public void EfModelSnapshot_ExposesUniqueIdempotencyKeyForPendingUploads()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var pendingEntity = db.Model.FindEntityType(typeof(WorkflowArtifactPendingUploadRow))!;
        var uniqueIndexes = pendingEntity.GetIndexes()
            .Where(i => i.IsUnique)
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToList();

        Assert.Contains("WorkflowRunId,WorkId,TaskRunId,Path", uniqueIndexes);
    }
}
