using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Grains;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

public partial class AgentSessionInputAttachmentAcceptanceSpecs
{
    [Fact]
    public async Task Followup_MissingSessionLeavesAttachmentPendingAndUnreadable()
    {
        var projectId = await CreateProjectAsync("followup-missing-att");
        var upload = await UploadAsync(projectId, "pending.txt", "text/plain", "pending"u8.ToArray());
        const string sessionId = "agent-session-missing";
        const string idempotencyKey = "missing-session-key";
        var inputId = AgentLaunchCoordinatorCodec.StableToken(
            $"{sessionId}\n{idempotencyKey}\nfollowup-input");

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/followup")
        {
            Content = JsonContent.Create(new { attachments = new[] { upload.Id } }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("not_found", data.GetProperty("code").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Attachments.AsNoTracking().SingleAsync(attachment => attachment.Id == upload.Id);
        Assert.Null(row.OwnerKind);
        Assert.NotNull(row.ExpiresAt);

        using var content = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/inputs/{inputId}/attachments/{upload.Id}/content");
        Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
    }

    [Fact]
    public async Task Followup_AttachmentRetryWithSameIdempotencyKey_ReplaysAcceptedInput()
    {
        var projectId = await CreateProjectAsync("followup-attachment-retry");
        var runnerId = $"followup-attachment-retry-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "followup-attachment-retry-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            var launch = await LaunchAsync(projectId, agent.Id, new { prompt = "first" });
            var launchData = (await launch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            var sessionId = launchData.GetProperty("sessionId").GetString()!;
            await CompleteLaunchAsync(runnerId, launchData);
            await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
                .AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-followup-attachment-retry"));

            var upload = await UploadAsync(projectId, "retry.txt", "text/plain", "retry"u8.ToArray());
            var idempotencyKey = Guid.NewGuid().ToString("N");

            Task<HttpResponseMessage> SubmitAsync()
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/api/projects/{projectId}/agent-sessions/{sessionId}/followup")
                {
                    Content = JsonContent.Create(new { attachments = new[] { upload.Id } }),
                };
                request.Headers.Add("Idempotency-Key", idempotencyKey);
                return _fixture.Client.SendAsync(request);
            }

            using var first = await SubmitAsync();
            using var second = await SubmitAsync();
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            var firstData = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            var secondData = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal(firstData.GetProperty("inputId").GetString(), secondData.GetProperty("inputId").GetString());
            Assert.Equal(upload.Id, Assert.Single(secondData.GetProperty("attachments").EnumerateArray()).GetProperty("id").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Followup_ConflictingAttachmentRetryLeavesNewAttachmentPending()
    {
        var projectId = await CreateProjectAsync("followup-attachment-conflict");
        var runnerId = $"followup-attachment-conflict-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "followup-attachment-conflict-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            var launch = await LaunchAsync(projectId, agent.Id, new { prompt = "first" });
            var launchData = (await launch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            var sessionId = launchData.GetProperty("sessionId").GetString()!;
            await CompleteLaunchAsync(runnerId, launchData);
            await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
                .AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-followup-attachment-conflict"));

            var accepted = await UploadAsync(projectId, "accepted.txt", "text/plain", "accepted"u8.ToArray());
            var rejected = await UploadAsync(projectId, "rejected.txt", "text/plain", "rejected"u8.ToArray());
            var idempotencyKey = Guid.NewGuid().ToString("N");

            Task<HttpResponseMessage> SubmitAsync(string attachmentId)
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/api/projects/{projectId}/agent-sessions/{sessionId}/followup")
                {
                    Content = JsonContent.Create(new { attachments = new[] { attachmentId } }),
                };
                request.Headers.Add("Idempotency-Key", idempotencyKey);
                return _fixture.Client.SendAsync(request);
            }

            using var first = await SubmitAsync(accepted.Id);
            using var second = await SubmitAsync(rejected.Id);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var secondData = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("followup_rejected", secondData.GetProperty("code").GetString());

            await using var scope = _fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var rejectedRow = await db.Attachments.AsNoTracking().SingleAsync(attachment => attachment.Id == rejected.Id);
            Assert.Null(rejectedRow.OwnerKind);
            Assert.NotNull(rejectedRow.ExpiresAt);

            var inputId = AgentLaunchCoordinatorCodec.StableToken(
                $"{sessionId}\n{idempotencyKey}\nfollowup-input");
            using var content = await _fixture.Client.GetAsync(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}/inputs/{inputId}/attachments/{rejected.Id}/content");
            Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_ConcurrentSameKeyWithAttachment_PersistsOneOwnedInput()
    {
        var projectId = await CreateProjectAsync("launch-attachment-concurrent");
        var runnerId = $"launch-attachment-concurrent-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "launch-attachment-concurrent-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);
        var upload = await UploadAsync(projectId, "concurrent.txt", "text/plain", "content"u8.ToArray());
        var idempotencyKey = Guid.NewGuid().ToString("N");

        try
        {
            var responses = await Task.WhenAll(
                LaunchAsync(projectId, agent.Id, new { prompt = "read this", attachments = new[] { upload.Id } }, idempotencyKey),
                LaunchAsync(projectId, agent.Id, new { prompt = "read this", attachments = new[] { upload.Id } }, idempotencyKey));
            using var first = responses[0];
            using var second = responses[1];
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);

            var firstData = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            var secondData = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal(firstData.GetProperty("sessionId").GetString(), secondData.GetProperty("sessionId").GetString());
            Assert.Equal(firstData.GetProperty("inputId").GetString(), secondData.GetProperty("inputId").GetString());

            var sessionId = firstData.GetProperty("sessionId").GetString()!;
            var initial = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetInitialLaunchAsync();
            Assert.Equal(upload.Id, Assert.Single(initial!.Input!.Attachments!).Id);
            await CompleteLaunchAsync(runnerId, firstData);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }
}
