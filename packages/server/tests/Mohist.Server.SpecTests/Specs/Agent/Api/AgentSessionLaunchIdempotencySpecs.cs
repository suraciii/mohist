using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

[Collection("MohistIntegration")]
public class AgentSessionLaunchIdempotencySpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentSessionLaunchIdempotencySpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Launch_ReplayAfterAgentRename_ReturnsOriginalLaunch()
    {
        var projectId = await CreateProjectAsync("launch-replay-renamed-agent");
        var agent = await CreateAgentAsync(projectId, "original-agent-name");
        await CreateWorkspaceAsync(projectId, "launch-idempotency-workspace");
        const string idempotencyKey = "replay-after-agent-rename";

        using var first = await LaunchAsync(
            projectId,
            "original-agent-name",
            new { prompt = "preserve original launch", context = new { workspace = "launch-idempotency-workspace" } },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await LaunchReferencesAsync(first);

        using var rename = await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}",
            new { name = "renamed-agent" });
        rename.EnsureSuccessStatusCode();

        using var replay = await LaunchAsync(
            projectId,
            "original-agent-name",
            new { prompt = "preserve original launch", context = new { workspace = "launch-idempotency-workspace" } },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original, await LaunchReferencesAsync(replay));
    }

    [Fact]
    public async Task Launch_ReplayAfterAgentArchive_ReturnsOriginalLaunch()
    {
        var projectId = await CreateProjectAsync("launch-replay-archived-agent");
        var agent = await CreateAgentAsync(projectId, "replay-archived-agent");
        await CreateWorkspaceAsync(projectId, "launch-idempotency-workspace");
        const string idempotencyKey = "replay-after-agent-archive";

        using var first = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "preserve archived launch", context = new { workspace = "launch-idempotency-workspace" } },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await LaunchReferencesAsync(first);

        using var archive = await _fixture.Client.DeleteAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}");
        archive.EnsureSuccessStatusCode();

        using var replay = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "preserve archived launch", context = new { workspace = "launch-idempotency-workspace" } },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original, await LaunchReferencesAsync(replay));
    }

    [Fact]
    public async Task Launch_ReplayAfterWorkspaceArchive_ReturnsOriginalLaunch()
    {
        var projectId = await CreateProjectAsync("launch-replay-archived-workspace");
        var agent = await CreateAgentAsync(projectId, "replay-archived-workspace-agent");
        const string workspaceName = "launch-idempotency-workspace";
        await CreateWorkspaceAsync(projectId, workspaceName);
        const string idempotencyKey = "replay-after-workspace-archive";
        var runnerId = $"launch-replay-workspace-runner-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var first = await LaunchAsync(
                projectId,
                agent.Id,
                new { prompt = "preserve archived workspace launch", context = new { workspace = workspaceName } },
                idempotencyKey);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var original = await LaunchReferencesAsync(first);

            var dispatch = await PollDispatchForSessionAsync(original.JobId, runnerId, original.SessionId);
            using var variables = JsonDocument.Parse(dispatch.Dispatch.GetProperty("variables").GetString()!);
            var workspace = variables.RootElement.GetProperty("workspace");
            Assert.Equal(workspaceName, workspace.GetProperty("name").GetString());
            var repository = Assert.Single(workspace.GetProperty("repositories").EnumerateArray());
            Assert.Equal("main", repository.GetProperty("name").GetString());

            using var archive = await _fixture.Client.PostAsync(
                $"/api/projects/{projectId}/workspaces/{workspaceName}/close",
                content: null);
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

            using var replay = await LaunchAsync(
                projectId,
                agent.Id,
                new { prompt = "preserve archived workspace launch", context = new { workspace = workspaceName } },
                idempotencyKey);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(original, await LaunchReferencesAsync(replay));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_ReplayWithDifferentWorkspaceIdentity_Conflicts()
    {
        var projectId = await CreateProjectAsync("launch-replay-workspace-identity");
        var agent = await CreateAgentAsync(projectId, "workspace-identity-agent");
        await CreateWorkspaceAsync(projectId, "launch-idempotency-workspace-a");
        const string idempotencyKey = "different-workspace-identity";

        using var first = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "same prompt", context = new { workspace = "launch-idempotency-workspace-a" } },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var archive = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/workspaces/launch-idempotency-workspace-a/close",
            content: null);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        await CreateWorkspaceAsync(projectId, "launch-idempotency-workspace-b");

        using var replay = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "same prompt", context = new { workspace = "launch-idempotency-workspace-b" } },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var payload = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("launch_idempotency_conflict", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Launch_ImplicitCliWorkspaceAndExplicitArchivedWorkspaceConflictWithoutNewSideEffects()
    {
        var projectId = await CreateProjectAsync("launch-replay-cli-binding-mode");
        var agent = await CreateAgentAsync(projectId, "cli-binding-mode-agent");
        await CreateWorkspaceAsync(projectId, "cli-current");
        using var archive = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/workspaces/cli-current/close",
            content: null);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        var attachmentId = await UploadAttachmentAsync(projectId, "cli-binding.txt", "cli binding"u8.ToArray());
        const string idempotencyKey = "implicit-cli-explicit-archived-workspace";
        var implicitBody = new
        {
            prompt = "keep one canonical launch",
            attachments = new[] { attachmentId },
        };

        using var implicitLaunch = await LaunchCliAsync(projectId, agent.Id, implicitBody, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, implicitLaunch.StatusCode);
        var original = await LaunchReferencesAsync(implicitLaunch);
        var originalSession = _fixture.Grains.GetGrain<IAgentSessionGrain>(original.SessionId);
        var originalInput = await originalSession.GetInitialLaunchAsync();
        Assert.NotNull(originalInput);
        var originalInputRecord = originalInput!.Input;
        Assert.NotNull(originalInputRecord);
        Assert.Equal(original.InputId, originalInputRecord!.Id);
        Assert.Contains(originalInputRecord.Attachments ?? [], attachment => attachment.Id == attachmentId);
        var jobsBeforeConflict = await CountAgentJobsAsync(projectId);
        var sessionsBeforeConflict = await CountAgentLaunchSessionsAsync(projectId);

        using var explicitReplay = await LaunchCliAsync(
            projectId,
            agent.Id,
            new
            {
                prompt = "keep one canonical launch",
                attachments = new[] { attachmentId },
                context = new { workspace = "cli-current" },
            },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, explicitReplay.StatusCode);
        var conflict = await explicitReplay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("launch_idempotency_conflict", conflict.GetProperty("code").GetString());

        var afterConflict = await originalSession.GetInitialLaunchAsync();
        Assert.NotNull(afterConflict);
        Assert.Equal(original.InputId, afterConflict!.Input?.Id);
        Assert.Equal(original.TurnId, afterConflict.Turn?.Id);
        Assert.Equal(
            originalInputRecord.Attachments?.Select(attachment => attachment.Id),
            afterConflict.Input?.Attachments?.Select(attachment => attachment.Id));
        Assert.Equal(jobsBeforeConflict, await CountAgentJobsAsync(projectId));
        Assert.Equal(sessionsBeforeConflict, await CountAgentLaunchSessionsAsync(projectId));
    }

    [Fact]
    public async Task Launch_ResponseLossAfterPlanPersist_ReconcilesOneDurableAttachmentOwner()
    {
        var projectId = await CreateProjectAsync("launch-response-loss-after-plan");
        var agent = await CreateAgentAsync(projectId, "response-loss-agent");
        await CreateWorkspaceAsync(projectId, "launch-idempotency-workspace");
        var attachmentId = await UploadAttachmentAsync(projectId, "response-loss.txt", "response loss"u8.ToArray());
        const string idempotencyKey = "response-loss-after-plan-persist";
        _fixture.LaunchFaults.CancelAfterPlanPersistOnce();

        using var response = await LaunchAsync(
            projectId,
            agent.Id,
            new
            {
                prompt = "reconcile the saved plan",
                attachments = new[] { attachmentId },
                context = new { workspace = "launch-idempotency-workspace" },
            },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var refs = await LaunchReferencesAsync(response);
        var initial = await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(refs.SessionId)
            .GetInitialLaunchAsync();
        Assert.NotNull(initial);
        var initialInput = initial!.Input;
        Assert.NotNull(initialInput);
        Assert.Equal(refs.InputId, initialInput!.Id);
        Assert.Equal(refs.TurnId, initial.Turn?.Id);
        Assert.Contains(initialInput.Attachments ?? [], attachment => attachment.Id == attachmentId);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var attachment = await db.Attachments.SingleAsync(row => row.Id == attachmentId);
        Assert.Equal(AttachmentService.OwnerKindAgentInput, attachment.OwnerKind);
        Assert.Equal(
            AttachmentService.BuildAgentInputOwnerId(refs.SessionId, refs.InputId),
            attachment.OwnerId);
    }

    [Fact]
    public async Task Launch_ReplayWithDifferentAttachments_Conflicts()
    {
        var projectId = await CreateProjectAsync("launch-replay-attachments");
        var agent = await CreateAgentAsync(projectId, "attachments-idempotency-agent");
        await CreateWorkspaceAsync(projectId, "launch-idempotency-workspace");
        var firstAttachment = await UploadAttachmentAsync(projectId, "first.txt", "first"u8.ToArray());
        var secondAttachment = await UploadAttachmentAsync(projectId, "second.txt", "second"u8.ToArray());
        const string idempotencyKey = "different-attachments";

        using var first = await LaunchAsync(
            projectId,
            agent.Id,
            new
            {
                prompt = "same prompt",
                attachments = new[] { firstAttachment },
                context = new { workspace = "launch-idempotency-workspace" },
            },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var replay = await LaunchAsync(
            projectId,
            agent.Id,
            new
            {
                prompt = "same prompt",
                attachments = new[] { secondAttachment },
                context = new { workspace = "launch-idempotency-workspace" },
            },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var payload = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("launch_idempotency_conflict", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Launch_ReplayPreservesAcceptedAndRejectedAttachmentResponse()
    {
        var projectId = await CreateProjectAsync("launch-replay-attachment-response");
        var agent = await CreateAgentAsync(projectId, "attachment-response-agent");
        const string workspaceName = "launch-idempotency-workspace";
        await CreateWorkspaceAsync(projectId, workspaceName);
        var acceptedAttachment = await UploadAttachmentAsync(projectId, "accepted.txt", "accepted"u8.ToArray());
        const string rejectedAttachment = "att_does_not_exist_for_replay";
        const string idempotencyKey = "replay-attachment-response";
        var request = new
        {
            prompt = "preserve attachment response",
            attachments = new[] { acceptedAttachment, rejectedAttachment },
            context = new { workspace = workspaceName },
        };

        using var first = await LaunchAsync(projectId, agent.Id, request, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstPayload = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstData = firstPayload.GetProperty("data");
        var firstAccepted = firstData.GetProperty("attachments").GetRawText();
        var firstRejected = firstData.GetProperty("rejectedAttachments").GetRawText();
        Assert.Contains(acceptedAttachment, firstAccepted, StringComparison.Ordinal);
        Assert.Contains(rejectedAttachment, firstRejected, StringComparison.Ordinal);

        using var replay = await LaunchAsync(projectId, agent.Id, request, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var replayPayload = await replay.Content.ReadFromJsonAsync<JsonElement>();
        var replayData = replayPayload.GetProperty("data");

        Assert.Equal(firstAccepted, replayData.GetProperty("attachments").GetRawText());
        Assert.Equal(firstRejected, replayData.GetProperty("rejectedAttachments").GetRawText());
    }

    [Fact]
    public async Task Launch_ReplayWithDifferentSuppliedAgentReference_Conflicts()
    {
        var projectId = await CreateProjectAsync("launch-replay-agent-reference");
        var agent = await CreateAgentAsync(projectId, "same-agent-different-reference");
        await CreateWorkspaceAsync(projectId, "launch-idempotency-workspace");
        const string idempotencyKey = "different-agent-reference";

        using var first = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "same prompt", context = new { workspace = "launch-idempotency-workspace" } },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var replay = await LaunchAsync(
            projectId,
            "same-agent-different-reference",
            new { prompt = "same prompt", context = new { workspace = "launch-idempotency-workspace" } },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var payload = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("launch_idempotency_conflict", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Launch_ReplayWithWhitespacePrompt_ConflictsInsteadOfRevalidating()
    {
        var projectId = await CreateProjectAsync("launch-replay-whitespace-prompt");
        var agent = await CreateAgentAsync(projectId, "whitespace-prompt-agent");
        await CreateWorkspaceAsync(projectId, "launch-idempotency-workspace");
        const string idempotencyKey = "different-whitespace-prompt";

        using var first = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "accepted prompt", context = new { workspace = "launch-idempotency-workspace" } },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var replay = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "   ", context = new { workspace = "launch-idempotency-workspace" } },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var payload = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("launch_idempotency_conflict", payload.GetProperty("code").GetString());
    }

    /// <summary>
    /// A participant failure at each coordinator fence must surface as a
    /// <c>503 launch_setup_pending</c> response from the launch route
    /// (never <c>201</c>), and the same Idempotency-Key must recover to a
    /// single accepted launch once the failure clears. One fact per fence
    /// so a regression at any boundary is isolated.
    /// </summary>
    [Theory]
    [InlineData(LaunchParticipantGate.PrepareJob)]
    [InlineData(LaunchParticipantGate.EnsureInitialLaunch)]
    [InlineData(LaunchParticipantGate.SubmitJob)]
    public async Task Launch_ParticipantFailureAtFence_Returns503AndRecoversWithSameKey(
        LaunchParticipantGate gate)
    {
        var projectId = await CreateProjectAsync($"launch-fence-{gate}");
        var agent = await CreateAgentAsync(projectId, "fence-agent");
        await CreateWorkspaceAsync(projectId, "launch-idempotency-workspace");
        var idempotencyKey = $"fence-{gate}-{Guid.NewGuid():N}";
        var body = new { prompt = "recover across the fence", context = new { workspace = "launch-idempotency-workspace" } };
        var runnerId = gate == LaunchParticipantGate.SubmitJob
            ? $"launch-fence-submit-runner-{Guid.NewGuid():N}"
            : null;

        if (runnerId is not null)
            await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);
        string? dispatchJobId = null;

        try
        {
            _fixture.LaunchFaults.FailNext(gate, times: 1);

            using var failing = await LaunchAsync(projectId, agent.Id, body, idempotencyKey);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failing.StatusCode);
            Assert.Equal(
                "launch_setup_pending",
                (await failing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            using var recovered = await LaunchAsync(projectId, agent.Id, body, idempotencyKey);
            Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
            var original = await LaunchReferencesAsync(recovered);
            dispatchJobId = original.JobId;

            // The fence no longer fails, so a resume with the same key must
            // return the persisted outcome rather than a new launch.
            _fixture.LaunchFaults.StopFailing(gate);
            using var resumed = await LaunchAsync(projectId, agent.Id, body, idempotencyKey);
            Assert.Equal(HttpStatusCode.Created, resumed.StatusCode);
            Assert.Equal(original, await LaunchReferencesAsync(resumed));
            Assert.Single(_fixture.LaunchFaults.CommandIds(gate).Distinct(StringComparer.Ordinal));
            if (runnerId is not null)
                Assert.Equal(1, _fixture.AgentJobDispatches.PreparedCount(original.JobId));

            await AssertInitialLaunchChildStateAsync(
                original,
                inputAcceptance: AgentSessionInputAcceptance.Accepted,
                turnStatus: AgentTurnStatus.Queued);
        }
        finally
        {
            if (runnerId is not null)
            {
                if (dispatchJobId is not null)
                    await CompletePendingAgentJobAsync(runnerId, dispatchJobId);
                await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
            }
        }
    }

    /// <summary>
    /// A launch blocked at the Session fence leaves a prepared Job but
    /// no accepted Input/Turn; recovery must not duplicate either child.
    /// Verifies the partial-state shape the review calls out: the
    /// recovered Session holds exactly one accepted Input and one Turn.
    /// </summary>
    [Fact]
    public async Task Launch_RecoveryAfterSessionFenceFailure_RecordsSingleInputAndTurn()
    {
        var projectId = await CreateProjectAsync("launch-fence-session-children");
        var agent = await CreateAgentAsync(projectId, "session-children-agent");
        await CreateWorkspaceAsync(projectId, "launch-idempotency-workspace");
        var idempotencyKey = $"fence-session-{Guid.NewGuid():N}";
        var body = new { prompt = "single input and turn after recovery", context = new { workspace = "launch-idempotency-workspace" } };

        _fixture.LaunchFaults.FailNext(LaunchParticipantGate.EnsureInitialLaunch, times: 2);
        for (var i = 0; i < 2; i++)
        {
            using var pending = await LaunchAsync(projectId, agent.Id, body, idempotencyKey);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
        }

        _fixture.LaunchFaults.StopFailing(LaunchParticipantGate.EnsureInitialLaunch);
        using var recovered = await LaunchAsync(projectId, agent.Id, body, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        var refs = await LaunchReferencesAsync(recovered);

        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(refs.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        Assert.NotNull(initial);
        Assert.Equal(refs.InputId, initial!.Input?.Id);
        Assert.Equal(AgentSessionInputAcceptance.Accepted, initial.Input?.Acceptance);
        Assert.Equal(refs.TurnId, initial.Turn?.Id);
        Assert.Equal(AgentTurnStatus.Queued, initial.Turn?.Status);
    }

    private async Task AssertInitialLaunchChildStateAsync(
        LaunchReferences refs,
        AgentSessionInputAcceptance inputAcceptance,
        AgentTurnStatus turnStatus)
    {
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(refs.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        Assert.NotNull(initial);
        Assert.Equal(refs.InputId, initial!.Input?.Id);
        Assert.Equal(inputAcceptance, initial.Input?.Acceptance);
        Assert.Equal(refs.TurnId, initial.Turn?.Id);
        Assert.Equal(turnStatus, initial.Turn?.Status);
    }

    private static async Task<LaunchReferences> LaunchReferencesAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        return new LaunchReferences(
            data.GetProperty("jobId").GetString() ?? string.Empty,
            data.GetProperty("sessionId").GetString() ?? string.Empty,
            data.GetProperty("inputId").GetString() ?? string.Empty,
            data.GetProperty("turnId").GetString() ?? string.Empty);
    }

    private async Task<string> UploadAttachmentAsync(string projectId, string fileName, byte[] payload)
    {
        using var form = new MultipartFormDataContent("----mohist-launch-idempotency-" + Guid.NewGuid().ToString("N"));
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(content, "file", fileName);

        using var response = await _fixture.Client.PostAsync($"/api/projects/{projectId}/attachments", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()!;
    }

    private sealed record LaunchReferences(string JobId, string SessionId, string InputId, string TurnId);
}
