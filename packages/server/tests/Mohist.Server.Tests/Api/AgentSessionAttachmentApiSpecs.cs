using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Api;

[Trait("level", "L1")]
public class AgentSessionAttachmentApiSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentSessionAttachmentApiSpecs(DefaultMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ScopedContentRoute_ReturnsContentForOwningSessionAndInput()
    {
        var projectId = await CreateProjectAsync("agent-input-att-ok");
        var sessionId = "session-owning";
        var inputId = "input-owning";
        var upload = await UploadAsync(projectId, "note.txt", "text/plain", "hello-session"u8.ToArray());

        await BindAsync(projectId, sessionId, inputId, [upload.Id]);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/inputs/{inputId}/attachments/{upload.Id}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("note.txt", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("hello-session", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ScopedContentRoute_ReturnsNotFoundForDifferentSession()
    {
        var projectId = await CreateProjectAsync("agent-input-att-other-session");
        var sessionId = "session-owner";
        var otherSessionId = "session-other";
        var inputId = "input-x";
        var upload = await UploadAsync(projectId, "private.txt", "text/plain", "private-body"u8.ToArray());

        await BindAsync(projectId, sessionId, inputId, [upload.Id]);

        using var wrongSession = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agent-sessions/{otherSessionId}/inputs/{inputId}/attachments/{upload.Id}/content");
        Assert.Equal(HttpStatusCode.NotFound, wrongSession.StatusCode);

        using var wrongInput = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/inputs/{inputId}-other/attachments/{upload.Id}/content");
        Assert.Equal(HttpStatusCode.NotFound, wrongInput.StatusCode);

        using var wrongAttachment = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/inputs/{inputId}/attachments/att_missing/content");
        Assert.Equal(HttpStatusCode.NotFound, wrongAttachment.StatusCode);
    }

    [Fact]
    public async Task ScopedContentRoute_ReturnsNotFoundForUnboundAttachment()
    {
        var projectId = await CreateProjectAsync("agent-input-att-unbound");
        var sessionId = "session-x";
        var inputId = "input-x";
        var unbound = await UploadAsync(projectId, "pending.txt", "text/plain", "still-pending"u8.ToArray());

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/inputs/{inputId}/attachments/{unbound.Id}/content");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == unbound.Id);
        Assert.Null(row.OwnerKind);
        Assert.NotNull(row.ExpiresAt);
    }

    private async Task BindAsync(string projectId, string sessionId, string inputId, IReadOnlyCollection<string> attachmentIds)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var attachments = scope.ServiceProvider.GetRequiredService<AttachmentService>();
        await attachments.BindAgentInputAsync(projectId, sessionId, inputId, attachmentIds);
    }

    private async Task<AttachmentUploadResponse> UploadAsync(string projectId, string fileName, string contentType, byte[] payload)
    {
        using var form = new MultipartFormDataContent("----mohist-agent-input-test-" + Guid.NewGuid().ToString("N"));
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "file", fileName);
        return await _fixture.Client.PostMultipartDataAsync<AttachmentUploadResponse>(
            $"/api/projects/{projectId}/attachments", form);
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var projectId = $"project-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"{prefix}-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = $"file://{Guid.NewGuid():N}",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        return projectId;
    }

    private sealed record AttachmentUploadResponse(
        string Id,
        string FileName,
        string? ContentType,
        long Size,
        string? ExpiresAt);
}
