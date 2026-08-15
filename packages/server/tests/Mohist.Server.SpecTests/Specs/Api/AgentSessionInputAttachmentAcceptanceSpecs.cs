using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public partial class AgentSessionInputAttachmentAcceptanceSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentSessionInputAttachmentAcceptanceSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Launch_AttachmentsOnly_AcceptsAndReturnsDescriptorsWithoutText()
    {
        var projectId = await CreateProjectAsync("launch-attachments-only");
        var runnerId = $"launch-attachments-only-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "attachments-only-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);
        var upload = await UploadAsync(projectId, "notes.txt", "text/plain", "hello"u8.ToArray());

        try
        {
            var response = await LaunchAsync(projectId, agent.Id, new
            {
                attachments = new[] { upload.Id },
            });

            if (response.StatusCode != HttpStatusCode.Created)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Launch expected 201 but got {(int)response.StatusCode} {response.ReasonPhrase}: {errorBody}");
            }

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            await CompleteLaunchAsync(runnerId, data);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_AttachmentContentIsAvailableThroughReturnedSessionAndInputScope()
    {
        var projectId = await CreateProjectAsync("launch-attachment-content");
        var runnerId = $"launch-attachment-content-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "attachment-content-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);
        var upload = await UploadAsync(projectId, "scope.txt", "text/plain", "scoped-content"u8.ToArray());

        try
        {
            var launch = await LaunchAsync(projectId, agent.Id, new
            {
                prompt = "read the attachment",
                attachments = new[] { upload.Id },
            });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var body = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var data = body.GetProperty("data");
            var sessionId = data.GetProperty("sessionId").GetString();
            var inputId = data.GetProperty("inputId").GetString();

            using var content = await _fixture.Client.GetAsync(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}/inputs/{inputId}/attachments/{upload.Id}/content");
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.Equal("scoped-content", await content.Content.ReadAsStringAsync());

            using var summary = await _fixture.Client.GetAsync(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}");
            var summaryData = (await summary.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            var observed = summaryData.GetProperty("inputs")[0].GetProperty("attachments")[0];
            Assert.Equal("upload", observed.GetProperty("source").GetString());
            Assert.Equal("usable", observed.GetProperty("availability").GetString());

            await CompleteLaunchAsync(runnerId, data);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_EmptyTextAndNoAttachments_ReturnsInputRequired()
    {
        var projectId = await CreateProjectAsync("launch-empty-input");
        var agent = await CreateAgentAsync(projectId, "empty-input-agent");

        var response = await LaunchAsync(projectId, agent.Id, new
        {
            prompt = "   ",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("input_required", body.GetProperty("code").GetString());
        var details = body.GetProperty("details");
        var fields = details.GetProperty("fields").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("attachments", fields);
        Assert.Contains("prompt", fields);
    }

    [Fact]
    public async Task Launch_AcceptedAttachmentsSurviveGrainReload_AndAppearAsDescriptorsInDispatch()
    {
        var projectId = await CreateProjectAsync("launch-survives");
        var runnerId = $"launch-survives-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "survives-agent");
        var upload = await UploadAsync(projectId, "snapshot.txt", "text/plain", "SNAP"u8.ToArray());
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            var launchResponse = await LaunchAsync(projectId, agent.Id, new
            {
                prompt = "snapshot",
                attachments = new[] { upload.Id },
            });
            Assert.Equal(HttpStatusCode.Created, launchResponse.StatusCode);
            var launchBody = await launchResponse.Content.ReadFromJsonAsync<JsonElement>();
            var launchData = launchBody.GetProperty("data");
            var jobId = launchData.GetProperty("jobId").GetString()!;
            var sessionId = launchData.GetProperty("sessionId").GetString()!;

            var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
            var reactivated = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            await reactivated.GetAsync();
            var initial = await reactivated.GetInitialLaunchAsync();
            Assert.NotNull(initial);
            Assert.NotNull(initial!.Input);
            var descriptor = Assert.Single(initial.Input.Attachments!);
            Assert.Equal(upload.Id, descriptor.Id);
            Assert.Equal("snapshot.txt", descriptor.OriginalFileName);

            var snapshot = await PollDispatchForSessionAsync(runnerId, sessionId);
            var polled = await PollDispatchEnvelopeAsync(runnerId, snapshot.WorkId!);
            using var withDoc = JsonDocument.Parse(polled.GetProperty("with").GetString()!);
            var attachments = withDoc.RootElement.GetProperty("attachments").EnumerateArray().ToArray();
            var dispatchAttachment = Assert.Single(attachments);
            Assert.Equal(upload.Id, dispatchAttachment.GetProperty("id").GetString());
            Assert.Equal("snapshot.txt", dispatchAttachment.GetProperty("name").GetString());
            Assert.Equal("text/plain", dispatchAttachment.GetProperty("contentType").GetString());
            await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).ReportResultAsync(
                runnerId,
                snapshot.WorkId!,
                new WorkResult(Status: "completed", Message: "ok"));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Followup_AttachmentsOnly_AcceptsWithoutText()
    {
        var projectId = await CreateProjectAsync("followup-attachments-only");
        var runnerId = $"followup-attachments-only-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "followup-att-only-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            var launch = await LaunchAsync(projectId, agent.Id, new { prompt = "first" });
            var launchBody = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var launchData = launchBody.GetProperty("data");
            var sessionId = launchData.GetProperty("sessionId").GetString()!;
            await CompleteLaunchAsync(runnerId, launchData);

            var sessionGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            await sessionGrain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-followup-att"));

            var upload = await UploadAsync(projectId, "later.txt", "text/plain", "LATER"u8.ToArray());

            var response = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}/followup",
                new
                {
                    attachments = new[] { upload.Id },
                });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = body.GetProperty("data");
            Assert.Equal("accepted", data.GetProperty("status").GetString());
            Assert.NotNull(data.GetProperty("inputId").GetString());
            Assert.NotNull(data.GetProperty("turnId").GetString());
            var accepted = data.TryGetProperty("attachments", out var acceptedProp)
                ? acceptedProp.EnumerateArray().ToArray()
                : [];
            var single = Assert.Single(accepted);
            Assert.Equal(upload.Id, single.GetProperty("id").GetString());
            Assert.Equal("later.txt", single.GetProperty("name").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Followup_EmptyTextAndNoAttachments_ReturnsRejected()
    {
        var projectId = await CreateProjectAsync("followup-empty-input");
        var runnerId = $"followup-empty-input-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "followup-empty-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            var launch = await LaunchAsync(projectId, agent.Id, new { prompt = "first" });
            var launchBody = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var launchData = launchBody.GetProperty("data");
            var sessionId = launchData.GetProperty("sessionId").GetString()!;
            await CompleteLaunchAsync(runnerId, launchData);

            var sessionGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            await sessionGrain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-followup-empty"));

            var response = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}/followup",
                new { text = "" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = body.GetProperty("data");
            Assert.Equal("rejected", data.GetProperty("status").GetString());
            Assert.Equal("followup_input_required", data.GetProperty("code").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Followup_AlreadyBoundAttachment_Rejected()
    {
        var projectId = await CreateProjectAsync("followup-already-bound");
        var runnerId = $"followup-already-bound-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "followup-already-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            var launch = await LaunchAsync(projectId, agent.Id, new { prompt = "first" });
            var launchBody = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var launchData = launchBody.GetProperty("data");
            var sessionId = launchData.GetProperty("sessionId").GetString()!;
            var inputId = launchData.GetProperty("inputId").GetString()!;
            await CompleteLaunchAsync(runnerId, launchData);

            var sessionGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            await sessionGrain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-followup-already"));

            var upload = await UploadAsync(projectId, "taken.txt", "text/plain", "TAKEN"u8.ToArray());

            // Bind the attachment to the launch input (already consumed
            // by the launch — though launches do not currently take
            // attachments, the bind helper accepts any session/input
            // pair). The follow-up must surface the rejection.
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var attachments = scope.ServiceProvider.GetRequiredService<AttachmentService>();
                await attachments.BindAgentInputAsync(projectId, sessionId, inputId, [upload.Id]);
            }

            var response = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}/followup",
                new
                {
                    text = "use this",
                    attachments = new[] { upload.Id },
                });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = body.GetProperty("data");
            var accepted = data.TryGetProperty("attachments", out var acceptedProp)
                ? acceptedProp.EnumerateArray().ToArray()
                : [];
            Assert.Empty(accepted);
            var rejected = data.GetProperty("rejectedAttachments").EnumerateArray()
                .Select(e => new
                {
                    Id = e.GetProperty("id").GetString(),
                    Reason = e.GetProperty("reason").GetString(),
                })
                .ToArray();
            var single = Assert.Single(rejected);
            Assert.Equal(upload.Id, single.Id);
            Assert.Equal("AlreadyBound", single.Reason);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private async Task CompleteLaunchAsync(string runnerId, JsonElement launchData)
    {
        var jobId = launchData.GetProperty("jobId").GetString()!;
        var sessionId = launchData.GetProperty("sessionId").GetString()!;
        var dispatch = await PollDispatchForSessionAsync(runnerId, sessionId);
        await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).ReportResultAsync(
            runnerId,
            dispatch.WorkId!,
            new WorkResult(Status: "completed", Message: "ok"));
    }
}
