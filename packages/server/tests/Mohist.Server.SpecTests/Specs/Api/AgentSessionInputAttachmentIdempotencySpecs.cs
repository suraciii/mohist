using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Sessions.Grains;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

public partial class AgentSessionInputAttachmentAcceptanceSpecs
{
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
