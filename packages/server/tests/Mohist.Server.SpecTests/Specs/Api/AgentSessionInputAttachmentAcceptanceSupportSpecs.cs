using Mohist.Server.SpecTests.Support;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.TestSupport;
using Orleans;

namespace Mohist.Server.SpecTests.Specs.Api;

public partial class AgentSessionInputAttachmentAcceptanceSpecs
{
    private const string LaunchWorkspaceName = "attachment-launch-workspace";
    private const string LaunchRepositoryName = "main";

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
        await _fixture.Client.PostOkAsync($"/api/projects/{projectId}/workspaces", new
        {
            name = LaunchWorkspaceName,
            repos = new[] { LaunchRepositoryName },
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

    private Task<HttpResponseMessage> LaunchAsync(
        string projectId,
        string agentId,
        object body,
        string? idempotencyKey = null)
    {
        var requestBody = JsonSerializer.SerializeToNode(body)?.AsObject()
            ?? throw new InvalidOperationException("Launch body must serialize as an object");
        var context = requestBody["context"] as JsonObject ?? new JsonObject();
        context["workspace"] = LaunchWorkspaceName;
        context["repository"] = LaunchRepositoryName;
        requestBody["context"] = context;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/agents/{agentId}/sessions")
        {
            Content = JsonContent.Create(requestBody),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
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
                    WorkId: data.GetProperty("workId").GetString() ?? string.Empty,
                    Dispatch: data.Clone());
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

    private sealed record AgentRef(string Id, string Name);

    private sealed record UploadResult(string Id, string FileName);

    private sealed record PollSnapshot(string WorkflowRunId, string WorkId, JsonElement Dispatch);
}
