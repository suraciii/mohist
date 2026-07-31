using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class AgentSessionInputAttachmentAcceptanceSpecs
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
    public async Task Launch_MixedAcceptAndReject_ReportsEachFileWithReason()
    {
        var projectId = await CreateProjectAsync("launch-mixed");
        var agent = await CreateAgentAsync(projectId, "mixed-agent");
        var valid = await UploadAsync(projectId, "good.txt", "text/plain", "ok"u8.ToArray());
        var unreadable = await UploadAsync(projectId, "readable.bin", "application/octet-stream", "DATA"u8.ToArray());

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var storage = (InMemoryAttachmentStorage)scope.ServiceProvider
                .GetRequiredService<Mohist.Server.Workflow.Storage.IAttachmentStorage>();
            storage.MarkUnreadable(storage.GenerateStoragePath(projectId, unreadable.Id));
        }

        const string missing = "att_does_not_exist";

        var response = await LaunchAsync(projectId, agent.Id, new
        {
            prompt = "process the upload",
            attachments = new[] { valid.Id, missing, unreadable.Id },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");

        var accepted = data.GetProperty("attachments").EnumerateArray().ToArray();
        var single = Assert.Single(accepted);
        Assert.Equal(valid.Id, single.GetProperty("id").GetString());
        Assert.Equal("good.txt", single.GetProperty("name").GetString());

        var rejected = data.GetProperty("rejectedAttachments").EnumerateArray()
            .Select(e => new
            {
                Id = e.GetProperty("id").GetString(),
                Reason = e.GetProperty("reason").GetString(),
            })
            .ToArray();
        Assert.Equal(2, rejected.Length);
        Assert.Contains(rejected, r => r.Id == missing && r.Reason == "NotFound");
        Assert.Contains(rejected, r => r.Id == unreadable.Id && r.Reason == "NotReadable");

        var sessionId = data.GetProperty("sessionId").GetString()!;
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var initial = await grain.GetInitialLaunchAsync();
        Assert.NotNull(initial);
        Assert.Equal("process the upload", initial!.Input!.Text);
        var stored = Assert.Single(initial.Input.Attachments!);
        Assert.Equal(valid.Id, stored.Id);

        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var unreadableRow = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == unreadable.Id);
        Assert.Null(unreadableRow.OwnerKind);
    }

    [Fact]
    public async Task Launch_AlreadyBoundAttachment_RejectedForDifferentInput()
    {
        var projectId = await CreateProjectAsync("launch-already-bound");
        var agent = await CreateAgentAsync(projectId, "already-bound-agent");
        var upload = await UploadAsync(projectId, "private.txt", "text/plain", "secret"u8.ToArray());

        const string otherSession = "session-other";
        const string otherInput = "input-other";
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var attachments = scope.ServiceProvider.GetRequiredService<AttachmentService>();
            await attachments.BindAgentInputAsync(projectId, otherSession, otherInput, [upload.Id]);
        }

        var response = await LaunchAsync(projectId, agent.Id, new
        {
            prompt = "use this attachment",
            attachments = new[] { upload.Id },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        var accepted = data.GetProperty("attachments").EnumerateArray().ToArray();
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

        await using var verify = _fixture.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == upload.Id);
        Assert.Equal(AttachmentService.OwnerKindAgentInput, row.OwnerKind);
        Assert.Equal(AttachmentService.BuildAgentInputOwnerId(otherSession, otherInput), row.OwnerId);
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
            var sessionId = launchBody.GetProperty("data").GetProperty("sessionId").GetString()!;

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
            var sessionId = launchBody.GetProperty("data").GetProperty("sessionId").GetString()!;

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
            var sessionId = launchBody.GetProperty("data").GetProperty("sessionId").GetString()!;

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
            var sessionId = launchBody.GetProperty("data").GetProperty("sessionId").GetString()!;
            var inputId = launchBody.GetProperty("data").GetProperty("inputId").GetString()!;

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

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var response = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<JsonElement>(
            "/api/projects", name);
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

    private async Task<AgentRef> CreateAgentAsync(string projectId, string name)
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = new { model = "openai/gpt-5.6" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, name);
    }

    private async Task RegisterRunnerAndAwaitOnlineAsync(string runnerId, string projectId)
    {
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId,
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 2 });

        var runnerGrain = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await TestWait.ForAsync(
            () => runnerGrain.GetRuntimeStateAsync(),
            s => s.Status == RunnerStatus.Online,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            $"Runner '{runnerId}' to reach Online");
    }

    private Task<HttpResponseMessage> LaunchAsync(string projectId, string agentId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/agents/{agentId}/sessions")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return _fixture.Client.SendAsync(request);
    }

    private async Task<UploadResult> UploadAsync(string projectId, string fileName, string contentType, byte[] payload)
    {
        using var form = new MultipartFormDataContent("----mohist-agent-input-spec-" + Guid.NewGuid().ToString("N"));
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "file", fileName);
        var response = await _fixture.Client.PostAsync($"/api/projects/{projectId}/attachments", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new UploadResult(
            body.GetProperty("data").GetProperty("id").GetString()!,
            body.GetProperty("data").GetProperty("fileName").GetString()!);
    }

    private async Task SeedUnreadableAttachmentAsync(string projectId, string attachmentId, string fileName, string contentType)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var storage = (InMemoryAttachmentStorage)scope.ServiceProvider
            .GetRequiredService<Mohist.Server.Workflow.Storage.IAttachmentStorage>();
        var storagePath = storage.GenerateStoragePath(projectId, attachmentId);
        db.Attachments.Add(new AttachmentRow
        {
            Id = attachmentId,
            ProjectId = projectId,
            OwnerKind = null,
            OwnerId = null,
            OwnerIssueNumber = null,
            OriginalFileName = fileName,
            ContentType = contentType,
            Size = 4,
            StoragePath = storagePath,
            CreatedAt = _fixture.TimeProvider.GetUtcNow(),
            ExpiresAt = _fixture.TimeProvider.GetUtcNow().AddHours(24),
        });
        await db.SaveChangesAsync();

        // The InMemoryAttachmentStorage records the row but is told the
        // metadata is gone (the next ReadMetadataAsync returns null).
        // The AttachmentService surfaces this as NotReadable.
        storage.MarkUnreadable(storagePath);
    }

    private async Task<string> CreateProjectAndSeedUnreadableAttachmentAsync(string prefix, string fileName, string contentType)
    {
        var projectId = await CreateProjectAsync(prefix);
        var unreadableId = $"att_{Guid.NewGuid():N}";
        await SeedUnreadableAttachmentAsync(projectId, unreadableId, fileName, contentType);
        return unreadableId;
    }

    private async Task<PollSnapshot> PollDispatchForSessionAsync(string runnerId, string expectedSessionId)
    {
        return (await TestWait.ForAsync(
            () => PollDispatchOnceAsync(runnerId, expectedSessionId),
            found => found is not null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100),
            $"a polled dispatch on runner '{runnerId}' carrying AgentSessionId='{expectedSessionId}'",
            _fixture.ReleaseDispatchBackoffAsync))!;
    }

    private async Task<PollSnapshot?> PollDispatchOnceAsync(string runnerId, string expectedSessionId)
    {
        using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
        var dispatches = await poll.ReadDispatchElementsAsync();
        PollSnapshot? match = null;
        var others = new List<JsonElement>();
        foreach (var data in dispatches)
        {
            var polledSessionId = data.TryGetProperty("agentSessionId", out var sessionIdElement)
                && sessionIdElement.ValueKind != JsonValueKind.Null
                ? sessionIdElement.GetString()
                : null;
            if (match is null && polledSessionId == expectedSessionId)
            {
                match = new PollSnapshot(
                    WorkflowRunId: data.GetProperty("workflowRunId").GetString() ?? string.Empty,
                    WorkId: data.GetProperty("workId").GetString() ?? string.Empty);
            }
            else
            {
                others.Add(data);
            }
        }

        foreach (var other in others)
            await DrainDispatchElementAsync(runnerId, other);

        return match;
    }

    private async Task DrainDispatchElementAsync(string runnerId, JsonElement data)
    {
        var workId = data.GetProperty("workId").GetString();
        var ownerKind = data.TryGetProperty("ownerKind", out var ownerKindElement)
            && ownerKindElement.ValueKind != JsonValueKind.Null
            ? ownerKindElement.GetString()
            : null;

        if (!string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            return;

        var agentJobId = data.TryGetProperty("agentJobId", out var agentJobIdElement)
            && agentJobIdElement.ValueKind != JsonValueKind.Null
            ? agentJobIdElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(agentJobId) || string.IsNullOrWhiteSpace(workId))
            return;

        var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(agentJobId!);
        await jobGrain.ReportResultAsync(
            runnerId,
            workId!,
            new WorkResult(
                Status: "completed",
                Message: "drained",
                Output: JSON.DeserializeElement("{}"),
                ArtifactUploadIds: null,
                ExitCode: 0));
    }

    private async Task<JsonElement> PollDispatchEnvelopeAsync(string runnerId, string workId)
    {
        for (var i = 0; i < 50; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            var dispatches = await poll.ReadDispatchElementsAsync();
            foreach (var data in dispatches)
            {
                if (string.Equals(data.GetProperty("workId").GetString(), workId, StringComparison.Ordinal))
                    return data;
                await DrainDispatchElementAsync(runnerId, data);
            }
        }

        throw new InvalidOperationException($"No polled dispatch for workId '{workId}'");
    }

    private sealed record AgentRef(string Id, string Name);

    private sealed record UploadResult(string Id, string FileName);

    private sealed record PollSnapshot(string WorkflowRunId, string WorkId);
}