using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class AttachmentApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AttachmentApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UploadBindServeAndRemove_IssueAttachmentLifecycle()
    {
        var projectId = await CreateProjectAsync("att-life");
        var issueNumber = await CreateIssueAsync(projectId, "with attachment", "initial");
        var upload = await UploadAsync(projectId, "screen.png", "image/png", "PNGDATA"u8.ToArray());

        Assert.StartsWith("att_", upload.Id);
        Assert.NotNull(upload.ExpiresAt);

        await _fixture.Client.PatchDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{issueNumber}",
            new { body = $"before ![screen](att:{upload.Id}) after", attachmentIds = new[] { upload.Id } });

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == upload.Id);
            Assert.Equal("issue", row.OwnerKind);
            Assert.Null(row.ExpiresAt);
        }

        using (var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/issues/{issueNumber}/attachments/{upload.Id}/content"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("inline", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("screen.png", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
            Assert.Equal("PNGDATA", await response.Content.ReadAsStringAsync());
        }

        using (var remove = await _fixture.Client.DeleteAsync($"/api/projects/{projectId}/issues/{issueNumber}/attachments/{upload.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        }

        var issue = await _fixture.Client.GetDataAsync<JsonElement>($"/api/projects/{projectId}/issues/{issueNumber}");
        Assert.DoesNotContain($"att:{upload.Id}", issue.GetProperty("body").GetString());

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            Assert.False(await db.Attachments.AnyAsync(a => a.Id == upload.Id));
        }
    }

    [Fact]
    public async Task CommentAttachment_ServesSvgAsDownloadWithOriginalFilename()
    {
        var projectId = await CreateProjectAsync("att-cmt");
        var issueNumber = await CreateIssueAsync(projectId, "comment attachment", null);
        var upload = await UploadAsync(projectId, "vector.svg", "image/svg+xml", "<svg/>"u8.ToArray());

        var commentResponse = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{issueNumber}/comments",
            new { displayName = "Attachment tester", body = $"see [vector](att:{upload.Id})", attachmentIds = new[] { upload.Id } });
        var commentId = commentResponse.GetProperty("id").GetString()!;

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/comments/{commentId}/attachments/{upload.Id}/content");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("vector.svg", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UploadAndBind_RejectSizeAndCountLimit()
    {
        var projectId = await CreateProjectAsync("att-limits");
        var issueNumber = await CreateIssueAsync(projectId, "limits", null);

        using (var form = Multipart("too-big.txt", "text/plain", new byte[AttachmentStorageOptions.DefaultMaxFileBytes + 1]))
        using (var response = await _fixture.Client.PostAsync($"/api/projects/{projectId}/attachments", form))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            var sizeLimitJson = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("size limit", sizeLimitJson.GetProperty("error").GetString());
        }

        var ids = new List<string>();
        for (var i = 0; i < AttachmentStorageOptions.DefaultMaxCountPerOwner + 1; i++)
        {
            var upload = await UploadAsync(projectId, $"f{i}.txt", "text/plain", Encoding.UTF8.GetBytes(i.ToString()));
            ids.Add(upload.Id);
        }

        using var bind = await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}",
            new { body = string.Join(' ', ids.Select(id => $"[f](att:{id})")), attachmentIds = ids });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bind.StatusCode);
        var countLimitJson = await bind.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("per-owner limit", countLimitJson.GetProperty("error").GetString());
        Assert.Equal("attachment_count_limit_exceeded", countLimitJson.GetProperty("code").GetString());

        var issue = await _fixture.Client.GetDataAsync<JsonElement>($"/api/projects/{projectId}/issues/{issueNumber}");
        Assert.False(issue.TryGetProperty("body", out _));
    }

    [Fact]
    public async Task RejectedCommentAttachmentBind_DoesNotCreateComment()
    {
        var projectId = await CreateProjectAsync("att-cmt-reject");
        var issueNumber = await CreateIssueAsync(projectId, "comment reject", null);

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/comments",
            new { displayName = "Attachment tester", body = "dangling [file](att:att_missing)", attachmentIds = new[] { "att_missing" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var issue = await _fixture.Client.GetDataAsync<JsonElement>($"/api/projects/{projectId}/issues/{issueNumber}");
        Assert.Empty(issue.GetProperty("comments").EnumerateArray());
    }

    [Fact]
    public async Task Attachment_IsScopedToProjectAndPersistsInDatabase()
    {
        var projectId = await CreateProjectAsync("att-scope-a");
        var otherProjectId = await CreateProjectAsync("att-scope-b");
        var issueNumber = await CreateIssueAsync(projectId, "scope", null);
        var otherIssueNumber = await CreateIssueAsync(otherProjectId, "scope other", null);
        var upload = await UploadAsync(projectId, "note.txt", "text/plain", "hello"u8.ToArray());

        await _fixture.Client.PatchDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{issueNumber}",
            new { body = $"[note](att:{upload.Id})", attachmentIds = new[] { upload.Id } });

        using (var wrongProject = await _fixture.Client.GetAsync($"/api/projects/{otherProjectId}/issues/{otherIssueNumber}/attachments/{upload.Id}/content"))
        {
            Assert.Equal(HttpStatusCode.NotFound, wrongProject.StatusCode);
        }

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var persisted = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == upload.Id);
        Assert.NotNull(persisted);
        Assert.Equal(projectId, persisted!.ProjectId);
        Assert.False(string.IsNullOrWhiteSpace(persisted.StoragePath));
    }

    [Fact]
    public async Task CreateIssue_BindsPendingAttachments()
    {
        var projectId = await CreateProjectAsync("att-create");
        var upload = await UploadAsync(projectId, "screen.png", "image/png", "PNG"u8.ToArray());

        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new { title = "created with attachment", body = $"![screen](att:{upload.Id})", attachmentIds = new[] { upload.Id } });

        Assert.Single(issue.GetProperty("attachments").EnumerateArray());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == upload.Id);
        Assert.Equal("issue", row.OwnerKind);
        Assert.Null(row.OwnerId);
        Assert.Equal(issue.GetProperty("number").GetInt32(), row.OwnerIssueNumber);
        Assert.Null(row.ExpiresAt);
    }

    [Fact]
    public async Task UploadAsync_RejectsStreamThatExceedsDeclaredSizeLimit()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var storage = new InMemoryAttachmentStorage();
        var service = new AttachmentService(
            dbFactory,
            storage,
            new AttachmentStorageOptions { MaxFileBytes = 4 },
            _fixture.TimeProvider);

        await Assert.ThrowsAsync<AttachmentLimitException>(() => service.UploadAsync(
            "proj_limit",
            new TestFormFile("too-big.txt", "text/plain", declaredLength: 1, payload: "12345"u8.ToArray())));

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.False(await db.Attachments.AnyAsync(a => a.ProjectId == "proj_limit"));
        Assert.Equal(0, storage.Count);
    }

    [Fact]
    public async Task CleanupExpiredPending_RemovesRowsAndStoredContent()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var storage = new InMemoryAttachmentStorage();
        var service = new AttachmentService(
            dbFactory,
            storage,
            new AttachmentStorageOptions(),
            _fixture.TimeProvider);
        var storagePath = storage.GenerateStoragePath("proj_cleanup", "att_cleanup");
        await storage.WriteFileAsync(storagePath, new MemoryStream("old"u8.ToArray()), new AttachmentFileWrite
        {
            OriginalFileName = "old.txt",
            ContentType = "text/plain",
            Size = 3,
        }, _fixture.TimeProvider.GetUtcNow());

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Attachments.Add(new AttachmentRow
            {
                Id = "att_cleanup",
                ProjectId = "proj_cleanup",
                OriginalFileName = "old.txt",
                ContentType = "text/plain",
                Size = 3,
                StoragePath = storagePath,
                CreatedAt = _fixture.TimeProvider.GetUtcNow().AddDays(-2),
                ExpiresAt = _fixture.TimeProvider.GetUtcNow().AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var removed = await service.CleanupExpiredPendingAsync();

        Assert.Equal(1, removed);
        await using var verify = await dbFactory.CreateDbContextAsync();
        Assert.False(await verify.Attachments.AnyAsync(a => a.Id == "att_cleanup"));
        Assert.False(storage.Contains(storagePath));
    }

    private static MultipartFormDataContent Multipart(string fileName, string contentType, byte[] payload)
    {
        var form = new MultipartFormDataContent("----mohist-attachment-test-" + Guid.NewGuid().ToString("N"));
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "file", fileName);
        return form;
    }

    private async Task<AttachmentUploadResponse> UploadAsync(string projectId, string fileName, string contentType, byte[] payload)
    {
        using var form = Multipart(fileName, contentType, payload);
        return await _fixture.Client.PostMultipartDataAsync<AttachmentUploadResponse>($"/api/projects/{projectId}/attachments", form);
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var response = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<JsonElement>("/api/projects", $"{prefix}-{Guid.NewGuid():N}");
        var projectId = response.GetProperty("id").GetString()!;
        await _fixture.Client.PostOkAsync($"/api/projects/{projectId}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return projectId;
    }

    private async Task<int> CreateIssueAsync(string projectId, string title, string? body)
    {
        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new { title, body });
        return issue.GetProperty("number").GetInt32();
    }

    private sealed record AttachmentUploadResponse(
        string Id,
        string FileName,
        string? ContentType,
        long Size,
        string? ExpiresAt);

    private sealed class TestFormFile : IFormFile
    {
        private readonly byte[] _payload;

        public TestFormFile(string fileName, string contentType, long declaredLength, byte[] payload)
        {
            FileName = fileName;
            ContentType = contentType;
            Length = declaredLength;
            _payload = payload;
        }

        public string ContentType { get; }
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length { get; }
        public string Name => "file";
        public string FileName { get; }
        public void CopyTo(Stream target) => OpenReadStream().CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => OpenReadStream().CopyToAsync(target, cancellationToken);
        public Stream OpenReadStream() => new MemoryStream(_payload, writable: false);
    }
}
