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

/// <summary>
/// Route-level contract specs for the workflow-artifact read endpoints:
/// one file content stream, cross-issue context rejection (404), and the
/// list DTO naming. The latest-per-path / history / no-history-latest /
/// task-run filtering and the directory listing / entry-bytes calculation
/// live in <c>WorkflowArtifactQuerySpecs</c>.
/// </summary>
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
                DisplayName = "proposal.md",
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
                DisplayName = "specs",
            });
        await db.SaveChangesAsync();

        return (projectId, issueNumber, workflowRunId);
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

        var row = await db.WorkflowArtifacts
            .AsNoTracking()
            .FirstAsync(a => a.WorkflowRunId == workflowRunId && a.Kind == "file");
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

    [Fact]
    public async Task ArtifactContent_ReturnsFileContentStream()
    {
        var (projectId, issueNumber, workflowRunId) = await SetupWithArtifactsAsync();

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
    public async Task ArtifactList_DtoUsesWorkflowArtifactNaming()
    {
        var (projectId, issueNumber, _) = await SetupWithArtifactsAsync();

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().First();

        Assert.True(data.TryGetProperty("artifactId", out _));
        Assert.True(data.TryGetProperty("workflowRunId", out _));
        Assert.True(data.TryGetProperty("taskRunId", out _));
        Assert.True(data.TryGetProperty("path", out _));
        Assert.True(data.TryGetProperty("kind", out _));
    }
}
