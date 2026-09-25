using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Idempotency;
using Mohist.Server.Infrastructure.Idempotency;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Api;

[Trait("level", "L1")]
public sealed class WorkflowRunControlIdempotencyApiSpecs(DefaultMohistIntegrationFixture fixture)
    : IClassFixture<DefaultMohistIntegrationFixture>
{
    private HttpClient Client => fixture.Client;
    private IGrainFactory Grains => fixture.Grains;
    private IServiceProvider Services => fixture.Services;

    [Fact]
    public async Task RequestChanges_SameKeySameBody_ReplaysRecordedResponseWithoutSecondFeedback()
    {
        var wrId = await SeedAwaitingApprovalWorkflowAsync();

        var first = await SendRequestChangesAsync(wrId, "rc-key-1", "tighten the check");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();

        var replay = await SendRequestChangesAsync(wrId, "rc-key-1", "tighten the check");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await replay.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, replayBody);
        // The recorded answer is not an empty acknowledgement: it carries the
        // resource the control changed, so a caller that lost the first
        // response recovers the same facts on the replay.
        var payload = JsonDocument.Parse(firstBody).RootElement
            .GetProperty("data").GetProperty("status");
        Assert.Equal(wrId, payload.GetProperty("workflowRunId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("status").GetString()));
        var run = await LoadRunAsync(wrId);
        Assert.Single(run.Feedback);
        Assert.Equal(1, await FenceRowCountAsync(wrId));
    }

    [Fact]
    public async Task RequestChanges_SameKeyDifferentBody_Returns409ReusedWithoutSecondFeedback()
    {
        var wrId = await SeedAwaitingApprovalWorkflowAsync();

        await SendRequestChangesAsync(wrId, "rc-key-2", "tighten the check");

        var reused = await SendRequestChangesAsync(wrId, "rc-key-2", "loosen the check");

        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        var payload = await reused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_key_reused", payload.GetProperty("code").GetString());
        Assert.Equal("none", payload.GetProperty("effect").GetString());
        Assert.False(payload.GetProperty("retrySafe").GetBoolean());
        // The key already belongs to another request, so the recovery command
        // names the one the caller just sent: run that under a fresh key, or
        // resend the original body with this key.
        Assert.Equal(
            $"mo run request-changes {wrId} --message 'loosen the check' --idempotency-key <new-key>",
            payload.GetProperty("nextAction").GetString());

        var run = await LoadRunAsync(wrId);
        Assert.Single(run.Feedback);
        Assert.Equal(1, await FenceRowCountAsync(wrId));
    }

    [Fact]
    public async Task RequestChanges_DifferentKey_CreatesSecondFeedbackOperation()
    {
        var wrId = await SeedAwaitingApprovalWorkflowAsync();

        await SendRequestChangesAsync(wrId, "rc-key-3", "tighten the check");
        await CompleteApplyFeedbackTaskAsync(wrId);
        var runAfterFirstLoop = await LoadRunAsync(wrId);
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, runAfterFirstLoop.Status);

        var second = await SendRequestChangesAsync(wrId, "rc-key-4", "tighten the check");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Equal(2, run.Feedback.Count);
        Assert.Equal(2, await FenceRowCountAsync(wrId));
    }

    [Fact]
    public async Task RequestChanges_StateGuardRejection_IsRecordedAndReplayed()
    {
        var wrId = await SeedActiveWorkflowAsync();

        var rejected = await SendRequestChangesAsync(wrId, "rc-key-guard", "tighten the check");
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        var rejectedPayload = JsonDocument.Parse(rejectedBody).RootElement;
        Assert.Equal("conflict", rejectedPayload.GetProperty("code").GetString());
        Assert.Equal("none", rejectedPayload.GetProperty("effect").GetString());
        Assert.False(rejectedPayload.GetProperty("retrySafe").GetBoolean());
        var row = await FenceRowAsync(wrId, "rc-key-guard");
        Assert.Equal(IdempotencyMappingStates.Rejected, row.State);

        var replay = await SendRequestChangesAsync(wrId, "rc-key-guard", "tighten the check");
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(rejectedBody, await replay.Content.ReadAsStringAsync());

        var reused = await SendRequestChangesAsync(wrId, "rc-key-guard", "loosen the check");
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        var reusedPayload = await reused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_key_reused", reusedPayload.GetProperty("code").GetString());

        var fresh = await SendRequestChangesAsync(wrId, "rc-key-fresh", "tighten the check");
        Assert.Equal(HttpStatusCode.Conflict, fresh.StatusCode);
        Assert.Equal(2, await FenceRowCountAsync(wrId));
    }

    [Fact]
    public async Task WorkflowControl_KeyWhilePendingYoungerThanLease_Returns503OperationPending()
    {
        var wrId = await SeedActiveWorkflowAsync();
        const string key = "pause-key-pending";
        await SeedPendingFenceRowAsync(wrId, key, AgeSeconds: 0);

        var response = await SendPauseAsync(wrId, key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("1", response.Headers.RetryAfter?.ToString());
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("operation_pending", payload.GetProperty("code").GetString());
        Assert.Equal("unknown", payload.GetProperty("effect").GetString());
        Assert.True(payload.GetProperty("retrySafe").GetBoolean());
        Assert.Equal(
            $"mo run pause {wrId} --idempotency-key {key}",
            payload.GetProperty("nextAction").GetString());
        var run = await LoadRunAsync(wrId);
        Assert.NotEqual(WorkflowRunStatus.Paused, run.Status);
        var row = await FenceRowAsync(wrId, key);
        Assert.Equal(IdempotencyMappingStates.Pending, row.State);
    }

    [Fact]
    public async Task WorkflowControl_StalePendingRow_TakesOverAndExecutes()
    {
        var wrId = await SeedActiveWorkflowAsync();
        const string key = "pause-key-stale";
        await SeedPendingFenceRowAsync(wrId, key, AgeSeconds: 31);

        var response = await SendPauseAsync(wrId, key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Equal(WorkflowRunStatus.Paused, run.Status);
        var row = await FenceRowAsync(wrId, key);
        Assert.Equal(IdempotencyMappingStates.Completed, row.State);
    }

    [Fact]
    public async Task WorkflowControl_MalformedKey_Returns400InvalidBeforeAnyEffect()
    {
        var wrId = await SeedActiveWorkflowAsync();
        var malformedKey = new string('a', 129);

        var response = await SendPauseAsync(wrId, malformedKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_key_invalid", payload.GetProperty("code").GetString());
        Assert.Equal("none", payload.GetProperty("effect").GetString());
        Assert.False(payload.GetProperty("retrySafe").GetBoolean());
        var run = await LoadRunAsync(wrId);
        Assert.NotEqual(WorkflowRunStatus.Paused, run.Status);
        Assert.Equal(0, await FenceRowCountAsync(wrId));
    }

    [Fact]
    public async Task WorkflowControl_UnkeyedRequest_ExecutesWithoutFenceRow()
    {
        var wrId = await SeedActiveWorkflowAsync();

        var response = await Client.PostAsync($"/api/workflow-runs/{wrId}/pause", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Equal(WorkflowRunStatus.Paused, run.Status);
        Assert.Equal(0, await FenceRowCountAsync(wrId));
    }

    [Fact]
    public async Task Stop_PendingHintCarriesTheConfirmationFlagTheCliRequires()
    {
        var wrId = await SeedActiveWorkflowAsync();
        const string key = "stop-key-pending";
        await SeedPendingFenceRowAsync(wrId, key, AgeSeconds: 0, verb: "stop");

        var response = await SendStopAsync(wrId, key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            $"mo run stop {wrId} --yes --idempotency-key {key}",
            payload.GetProperty("nextAction").GetString());
        var run = await LoadRunAsync(wrId);
        Assert.NotEqual(WorkflowRunStatus.Stopped, run.Status);
    }

    [Fact]
    public async Task RerunFromStage_PendingHintCarriesTheStageAndTheRerunLeaf()
    {
        var wrId = await SeedActiveWorkflowAsync();
        const string key = "rerun-stage-key-pending";
        await SeedPendingFenceRowAsync(wrId, key, AgeSeconds: 0, verb: "rerun-from-stage", stage: "plan");

        var response = await SendRerunFromStageAsync(wrId, key, "plan");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            $"mo run rerun {wrId} --from-stage plan --idempotency-key {key}",
            payload.GetProperty("nextAction").GetString());
    }

    private static Task<HttpResponseMessage> SendRequestChangesAsync(HttpClient client, string wrId, string key, string message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workflow-runs/{wrId}/request-changes")
        {
            Content = JsonContent.Create(new { message }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendRequestChangesAsync(string wrId, string key, string message) =>
        SendRequestChangesAsync(Client, wrId, key, message);

    private Task<HttpResponseMessage> SendPauseAsync(string wrId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workflow-runs/{wrId}/pause");
        request.Headers.Add("Idempotency-Key", key);
        return Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendStopAsync(string wrId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workflow-runs/{wrId}/stop");
        request.Headers.Add("Idempotency-Key", key);
        return Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendRerunFromStageAsync(string wrId, string key, string stage)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workflow-runs/{wrId}/rerun-from-stage")
        {
            Content = JsonContent.Create(new { stage }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return Client.SendAsync(request);
    }

    private async Task SeedPendingFenceRowAsync(
        string wrId,
        string key,
        int AgeSeconds,
        string verb = "pause",
        string? stage = null)
    {
        var factory = Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.IdempotencyMappings.Add(new IdempotencyMappingRow
        {
            Command = IdempotencyCommands.WorkflowControl,
            ScopeKey = KeyedControlWrites.WorkflowControlScopeKey(wrId, "service", key),
            CallerKeyId = "service",
            Fingerprint = stage is null
                ? KeyedControlWrites.WorkflowControlFingerprint(wrId, verb)
                : KeyedControlWrites.WorkflowControlFingerprint(wrId, verb, new { stage }),
            State = IdempotencyMappingStates.Pending,
            CreatedAt = fixture.TimeProvider.GetUtcNow().AddSeconds(-AgeSeconds),
        });
        await db.SaveChangesAsync();
    }

    private async Task<IdempotencyMappingRow> FenceRowAsync(string wrId, string key)
    {
        var factory = Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.IdempotencyMappings.AsNoTracking().SingleAsync(row =>
            row.Command == IdempotencyCommands.WorkflowControl
            && row.ScopeKey == KeyedControlWrites.WorkflowControlScopeKey(wrId, "service", key));
    }

    private async Task<int> FenceRowCountAsync(string wrId)
    {
        var factory = Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return (await db.IdempotencyMappings.AsNoTracking()
            .Where(row => row.Command == IdempotencyCommands.WorkflowControl)
            .ToListAsync())
            .Count(row => row.ScopeKey.StartsWith(wrId + "|", StringComparison.Ordinal));
    }

    /// <summary>
    /// Drives the apply-feedback task of the open feedback loop to a
    /// successful report, which returns the single approval stage to the
    /// awaiting-approval state.
    /// </summary>
    private async Task CompleteApplyFeedbackTaskAsync(string wrId)
    {
        const string workerId = "spec-worker";
        var grain = Grains.GetGrain<IWorkflowGrain>(wrId);
        await grain.AssignWorkerAsync(workerId);
        var work = await grain.ClaimNextAsync(workerId, "spec-generation");
        Assert.NotNull(work);
        var run = await LoadRunAsync(wrId);
        var actionAttemptId = run.CurrentStage().RunningTask!.Id;
        await grain.ReceiveTaskReportAsync(
            workerId,
            work!.Id!,
            new TaskReport(work.Id!, TaskReportStatus.Succeeded, Output: null, Artifacts: null, ActionAttemptId: actionAttemptId));
    }

    private async Task<string> SeedAwaitingApprovalWorkflowAsync()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueKey, _) = await WorkflowApiTestSupport.CreateIssueInBacklogAsync(Grains, projectId);
        var definition = new WorkflowDefinition(
        [
            new StageDefinition("plan", [], [], RequiresApproval: true),
        ],
        Approval: new ApprovalConfig(new ApprovalFeedbackConfig([
            new TaskDefinition("apply-feedback", "Apply approval feedback", "spec/apply-feedback"),
        ])));
        await WorkflowApiTestSupport.SeedWorkflowProfileAsync(fixture.ConnectionString, projectId, definition);
        await DispatchEventsAsync();
        var wrId = await Grains.GetGrain<IIssueGrain>(issueKey).StartWorkAsync();
        var run = await LoadRunAsync(wrId);
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        return wrId;
    }

    private async Task<string> SeedActiveWorkflowAsync()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueKey, _) = await WorkflowApiTestSupport.CreateIssueInBacklogAsync(Grains, projectId);
        await WorkflowApiTestSupport.SeedWorkflowTemplateAsync(fixture.ConnectionString, projectId);
        await DispatchEventsAsync();
        return await Grains.GetGrain<IIssueGrain>(issueKey).StartWorkAsync();
    }

    private async Task<(string ProjectId, string Name)> SeedProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var name = $"wr-keyed-{Guid.NewGuid():N}";
        await Grains.GetGrain<IProjectGrain>(id).CreateAsync(name, new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:test.git",
            BaseBranch = "main",
            IsDefault = true,
        }, "git diff --check");
        return (id, name);
    }

    private Task DispatchEventsAsync() => WorkflowApiTestSupport.DispatchEventsAsync(Services);

    private Task<WorkflowRun> LoadRunAsync(string wrId) => WorkflowApiTestSupport.LoadRunAsync(Services, wrId);
}
