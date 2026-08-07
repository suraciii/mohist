using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class WorkflowArtifactQueryRouteSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public WorkflowArtifactQueryRouteSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private static string UniqueProjectName(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 1 + 32, 63)];

    private async Task<(string projectId, int issueNumber, string workflowRunId)> SetupWithArtifactsAsync()
    {
        var projectName = UniqueProjectName("artq");
        var projectResponse = await _fixture.Client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name = projectName,
                repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
            });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

        var issueResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "artifact query test", isDraft = false });
        var issueJson = await issueResponse.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issueJson.GetProperty("data").GetProperty("number").GetInt32();

        using var startResp = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/start", null);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        var issueStatus = await issueGrain.GetWorkflowStatusAsync();
        var workflowRunId = issueStatus!.WorkflowRunId!;
        Assert.False(string.IsNullOrEmpty(workflowRunId));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var baseTime = new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.Zero);

        db.WorkflowArtifacts.AddRange(
            new WorkflowArtifactRow
            {
                ArtifactId = $"art_proposal_{Guid.NewGuid():N}",
                WorkflowRunId = workflowRunId,
                TaskRunId = "proposal.1",
                Path = "openspec/changes/issue-55/proposal.md",
                RecordedAt = baseTime,
                ArtifactStoragePath = $"{workflowRunId}/tasks/proposal.1/artifacts/art_proposal/content",
                Kind = "file",
                ContentType = "text/markdown",
                Size = 100,
                ProjectId = projectId,
                IssueNumber = issueNumber,
                DisplayName = "proposal.md",
            },
            new WorkflowArtifactRow
            {
                ArtifactId = $"art_review_v1_{Guid.NewGuid():N}",
                WorkflowRunId = workflowRunId,
                TaskRunId = "ai-review.1",
                Path = "review.md",
                RecordedAt = baseTime.AddMinutes(5),
                ArtifactStoragePath = $"{workflowRunId}/tasks/ai-review.1/artifacts/art_review_v1/content",
                Kind = "file",
                ContentType = "text/markdown",
                Size = 200,
                ProjectId = projectId,
                IssueNumber = issueNumber,
                DisplayName = "review.md",
            },
            new WorkflowArtifactRow
            {
                ArtifactId = $"art_review_v2_{Guid.NewGuid():N}",
                WorkflowRunId = workflowRunId,
                TaskRunId = "ai-review.2",
                Path = "review.md",
                RecordedAt = baseTime.AddMinutes(10),
                ArtifactStoragePath = $"{workflowRunId}/tasks/ai-review.2/artifacts/art_review_v2/content",
                Kind = "file",
                ContentType = "text/markdown",
                Size = 300,
                ProjectId = projectId,
                IssueNumber = issueNumber,
                DisplayName = "review.md",
            },
            new WorkflowArtifactRow
            {
                ArtifactId = $"art_specs_{Guid.NewGuid():N}",
                WorkflowRunId = workflowRunId,
                TaskRunId = "design.1",
                Path = "specs/",
                RecordedAt = baseTime.AddMinutes(2),
                ArtifactStoragePath = $"{workflowRunId}/tasks/design.1/artifacts/art_specs/files",
                Kind = "directory",
                Size = 500,
                ProjectId = projectId,
                IssueNumber = issueNumber,
                DisplayName = "specs",
            });
        await db.SaveChangesAsync();

        return (projectId, issueNumber, workflowRunId);
    }

    [Fact]
    public async Task ArtifactList_ReturnsLatestPerPathByDefault()
    {
        var (projectId, issueNumber, _) = await SetupWithArtifactsAsync();

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().ToList();

        Assert.Equal(3, data.Count);

        var review = data.Single(a => a.GetProperty("path").GetString() == "review.md");
        Assert.Equal("ai-review.2", review.GetProperty("taskRunId").GetString());
        Assert.Equal("file", review.GetProperty("kind").GetString());
        Assert.True(review.TryGetProperty("artifactId", out _));
        Assert.True(review.TryGetProperty("recordedAt", out _));
    }

    [Fact]
    public async Task ArtifactList_PathHistoryReturnsAllVersionsInOrder()
    {
        var (projectId, issueNumber, _) = await SetupWithArtifactsAsync();

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts?path=review.md&history=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().ToList();

        Assert.Equal(2, data.Count);
        Assert.Equal("ai-review.1", data[0].GetProperty("taskRunId").GetString());
        Assert.Equal("ai-review.2", data[1].GetProperty("taskRunId").GetString());
    }

    [Fact]
    public async Task ArtifactList_PathWithoutHistoryReturnsOnlyLatestVersion()
    {
        // When the client queries with ?path=review.md but does not
        // request history, the response must collapse to the single
        // newest version for that path. Without this contract, every
        // path query returns the full history list.
        var (projectId, issueNumber, _) = await SetupWithArtifactsAsync();

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts?path=review.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().ToList();

        var single = Assert.Single(data);
        Assert.Equal("review.md", single.GetProperty("path").GetString());
        Assert.Equal("ai-review.2", single.GetProperty("taskRunId").GetString());
    }

    [Fact]
    public async Task ArtifactList_TaskRunFilterReturnsProducedArtifacts()
    {
        var (projectId, issueNumber, _) = await SetupWithArtifactsAsync();

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts?taskRunId=ai-review.1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().ToList();

        var single = Assert.Single(data);
        Assert.Equal("ai-review.1", single.GetProperty("taskRunId").GetString());
        Assert.Equal("review.md", single.GetProperty("path").GetString());
    }

    [Fact]
    public async Task ArtifactList_EmptyForIssueWithoutWorkflow()
    {
        var projectName = UniqueProjectName("no-wf");
        var projectResponse = await _fixture.Client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name = projectName,
                repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
            });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

        var issueResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "no workflow yet" });
        var issueJson = await issueResponse.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issueJson.GetProperty("data").GetProperty("number").GetInt32();

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Empty(data.EnumerateArray());
    }

    [Fact]
    public async Task ArtifactContent_ReturnsFileContentStream()
    {
        var (projectId, issueNumber, workflowRunId) = await SetupWithArtifactsAsync();

        // Seed storage content for the first file artifact
        var contentBytes = Encoding.UTF8.GetBytes("# Proposal\n\nExample proposal content");
        var artifactId = await SeedStorageContentAsync(workflowRunId, contentBytes);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts/{artifactId}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("# Proposal\n\nExample proposal content", body);
    }

    [Fact]
    public async Task ArtifactContent_RejectsArtifactOutsideIssueContext()
    {
        var (projectId, issueNumber, _) = await SetupWithArtifactsAsync();

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts/nonexistent_artifact_id/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ArtifactContent_DirectoryListingReturnsContainedFiles()
    {
        // The seeded setup already includes a directory-kind artifact
        // for "specs/". Persist its contained files on disk via the
        // storage service and then GET the content endpoint without
        // ?file= to verify the directory listing shape.
        var (projectId, issueNumber, workflowRunId) = await SetupWithArtifactsAsync();
        var artifactId = await SeedDirectoryStorageContentAsync(workflowRunId);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts/{artifactId}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        Assert.Equal("directory", data.GetProperty("kind").GetString());
        var entries = data.GetProperty("entries").EnumerateArray().ToList();
        var paths = entries.Select(e => e.GetProperty("relativePath").GetString()).ToList();
        Assert.Contains("a.md", paths);
        Assert.Contains("sub/b.md", paths);
    }

    [Fact]
    public async Task ArtifactContent_DirectoryEntryReturnsRecordedBytes()
    {
        // The seeded setup already includes a directory-kind artifact
        // for "specs/". Persist its contained files on disk via the
        // storage service and then GET the content endpoint with
        // ?file=a.md to fetch the recorded bytes of one contained
        // file.
        var (projectId, issueNumber, workflowRunId) = await SetupWithArtifactsAsync();
        var artifactId = await SeedDirectoryStorageContentAsync(workflowRunId);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts/{artifactId}/content?file=a.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetBytes("# alpha content\n"), bytes);
    }

    /// <summary>
    /// Writes a directory-kind artifact's contained files into the
    /// storage root so the content endpoint has something to serve.
    /// Returns the directory artifact's id so the test can request
    /// its content.
    /// </summary>
    private async Task<string> SeedDirectoryStorageContentAsync(string workflowRunId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var row = await db.WorkflowArtifacts
            .AsNoTracking()
            .FirstAsync(a => a.WorkflowRunId == workflowRunId && a.Kind == "directory");

        var storage = scope.ServiceProvider.GetRequiredService<IWorkflowArtifactStorage>();

        await storage.WriteDirectoryAsync(
            row.ArtifactStoragePath,
            new List<WorkflowArtifactDirectoryEntryInput>
            {
                new()
                {
                    RelativePath = "a.md",
                    Size = Encoding.UTF8.GetByteCount("# alpha content\n"),
                    OpenContent = () => new MemoryStream(Encoding.UTF8.GetBytes("# alpha content\n"), writable: false),
                },
                new()
                {
                    RelativePath = "sub/b.md",
                    Size = Encoding.UTF8.GetByteCount("# beta content\n"),
                    OpenContent = () => new MemoryStream(Encoding.UTF8.GetBytes("# beta content\n"), writable: false),
                },
            },
            new WorkflowArtifactFileWrite
            {
                SourcePath = row.Path,
                Size = row.Size ?? 0,
                ContentType = row.ContentType,
                ContentHash = row.ContentHash,
            },
            row.RecordedAt);

        return row.ArtifactId;
    }

    [Fact]
    public async Task ArtifactList_DirectoryAppearsAsCollection()
    {
        var (projectId, issueNumber, _) = await SetupWithArtifactsAsync();

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().ToList();

        var specs = data.Where(a => a.GetProperty("path").GetString() == "specs/").ToList();
        Assert.Single(specs);
        Assert.Equal("directory", specs[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ArtifactList_DtoUsesWorkflowArtifactNaming()
    {
        var (projectId, issueNumber, _) = await SetupWithArtifactsAsync();

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().First();

        // Response should use artifactId, not snapshotId or similar
        Assert.True(data.TryGetProperty("artifactId", out _));
        Assert.True(data.TryGetProperty("workflowRunId", out _));
        Assert.True(data.TryGetProperty("taskRunId", out _));
        Assert.True(data.TryGetProperty("path", out _));
        Assert.True(data.TryGetProperty("kind", out _));
    }

    /// <summary>
    /// Seeds artifact content on disk so the content endpoint can serve
    /// it. Returns the artifact id of the proposal artifact so callers
    /// can request its content.
    /// </summary>
    private async Task<string> SeedStorageContentAsync(string workflowRunId, byte[] content)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var rows = await db.WorkflowArtifacts
            .AsNoTracking()
            .Where(a => a.WorkflowRunId == workflowRunId && a.Kind == "file")
            .ToListAsync();

        var row = rows.First();
        var storage = scope.ServiceProvider.GetRequiredService<IWorkflowArtifactStorage>();

        await storage.WriteFileAsync(
            row.ArtifactStoragePath,
            new MemoryStream(content),
            new WorkflowArtifactFileWrite
            {
                SourcePath = row.Path,
                Size = content.Length,
                ContentType = row.ContentType,
                ContentHash = row.ContentHash,
            },
            row.RecordedAt);

        return row.ArtifactId;
    }
}
