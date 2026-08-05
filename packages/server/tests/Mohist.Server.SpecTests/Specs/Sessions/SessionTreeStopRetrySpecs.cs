using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Runner.Services.SignalR;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public sealed class SessionTreeStopRetrySpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SessionTreeStopRetrySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UnknownRetryKeepsFence_AndCompletedOperationDoesNotStopALaterTurn()
    {
        var projectId = await CreateProjectAsync("stop-retry");
        var unknownSessionId = $"stop-unknown-{Guid.NewGuid():N}";
        var unknown = await OpenSessionAsync(projectId, unknownSessionId);
        const string oldTurnId = "turn-stop-old";
        await unknown.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-stop-old", oldTurnId, "old turn", "test"));
        await unknown.MarkTurnExecutingAsync(oldTurnId);
        await unknown.MarkTurnTerminalAsync(oldTurnId, AgentTurnStatus.Unknown, null);

        var unknownData = await PostStopAsync(projectId, unknownSessionId, "stop-unknown-key");
        Assert.Equal("unknown", unknownData.GetProperty("status").GetString());
        Assert.True(unknownData.GetProperty("admissionFenceActive").GetBoolean());
        Assert.Equal(oldTurnId, unknownData.GetProperty("targets")[0].GetProperty("turnId").GetString());
        Assert.True((await _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId).GetAsync()).ActiveTreeStop);

        var laterSessionId = $"stop-later-{Guid.NewGuid():N}";
        var later = await OpenSessionAsync(projectId, laterSessionId);
        var completed = await PostStopAsync(projectId, laterSessionId, "stop-completed-key");
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        var laterTurnId = "turn-after-stop";
        await later.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-after-stop", laterTurnId, "later turn", "test"));

        var replay = await PostStopAsync(projectId, laterSessionId, "stop-completed-key");
        Assert.Equal(completed.GetProperty("operationId").GetString(), replay.GetProperty("operationId").GetString());
        var turns = await later.ListTurnsAsync();
        Assert.Equal(AgentTurnStatus.Queued, Assert.Single(turns, item => item.Id == laterTurnId).Status);

        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        Assert.DoesNotContain(hub.Invocations, item => item.Method == "CancelAgentSession");
    }

    [Fact]
    public async Task FrozenSnapshotKeepsMembershipTargetIdsTurnsAndBindingAfterSourceChanges()
    {
        var projectId = $"stop-frozen-{Guid.NewGuid():N}";
        var rootId = $"stop-frozen-root-{Guid.NewGuid():N}";
        var childId = $"stop-frozen-child-{Guid.NewGuid():N}";
        var root = await OpenSessionAsync(projectId, rootId);
        var child = await OpenSessionAsync(projectId, childId);
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        await AttachAsync(fence, child, projectId, rootId, childId);
        var snapshotCommand = new BeginSessionTreeStopSnapshotCommand(
            projectId,
            rootId,
            "stop-frozen-operation",
            "stop-frozen-key",
            "stop-frozen-fingerprint");

        var started = await fence.BeginStopSnapshotAsync(snapshotCommand);
        Assert.Equal(SessionTreeStopSnapshotDisposition.Started, started.Disposition);
        var snapshot = started.Snapshot!;
        var childTarget = Assert.Single(snapshot.Targets, item => item.SessionId == childId);
        await child.ResetAsync(new ResetAgentSessionCommand(
            "runtime-stop-frozen",
            "runtime-replaced",
            "opencode",
            (await child.GetAsync())!.BindingEpoch));
        await root.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-root-after-snapshot", "turn-root-after-snapshot", "later root turn", "test"));

        var replay = await fence.BeginStopSnapshotAsync(snapshotCommand);
        Assert.Equal(SessionTreeStopSnapshotDisposition.Replayed, replay.Disposition);
        Assert.Equal(snapshot.Membership, replay.Snapshot!.Membership);
        Assert.Equal(snapshot.Targets, replay.Snapshot.Targets);
        Assert.Equal(childTarget.TurnId, replay.Snapshot.Targets.Single(item => item.SessionId == childId).TurnId);
        Assert.Equal(childTarget.StopOperationId, replay.Snapshot.Targets.Single(item => item.SessionId == childId).StopOperationId);
        Assert.Equal(childTarget.BindingEpoch, replay.Snapshot.Targets.Single(item => item.SessionId == childId).BindingEpoch);
    }

    private async Task<JsonElement> PostStopAsync(string projectId, string sessionId, string key)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/stop");
        request.Headers.Add("Idempotency-Key", key);
        using var response = await _fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data");
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(63, prefix.Length + 33)];
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()!;
    }

    private async Task<IAgentSessionGrain> OpenSessionAsync(string projectId, string sessionId)
    {
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            "runner-stop-retry",
            "opencode",
            "/workspace",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "stop-agent",
            })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "runtime-stop-frozen",
            ExpectedRunnerId: "runner-stop-retry",
            ExpectedRuntime: "opencode"));
        return session;
    }

    private async Task AttachAsync(
        ISessionTreeMutationFenceGrain fence,
        IAgentSessionGrain child,
        string projectId,
        string parentId,
        string childId)
    {
        var command = new ReserveSessionTreeLinkCommand(
            projectId,
            "edge-stop-frozen",
            parentId,
            childId,
            "/workspace",
            "runner-stop-retry",
            "opencode",
            "runtime-stop-frozen",
            "command-stop-frozen",
            "job-stop-frozen",
            "stop-agent",
            1,
            SessionTreeExpectedLinkState.Absent);
        await fence.ReserveAsync(command);
        var parent = _fixture.Grains.GetGrain<IAgentSessionGrain>(parentId);
        var acquired = (await parent.AcquireChildAttachBindingAsync(new AcquireChildAttachBindingCommand(
            projectId,
            command.CommandId,
            command.EdgeId,
            parentId,
            command.ExpectedWorkDir,
            command.ExpectedRunnerId,
            command.ExpectedRuntime,
            command.ExpectedRuntimeSessionId,
            command.ExpectedBindingEpoch!.Value,
            command.ParentAgentId!))).Receipt!;
        var begun = await fence.BeginFinalizeAsync(command.CommandId, command.EdgeId, acquired);
        var applied = await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            command.CommandId,
            command.EdgeId,
            parentId,
            command.ParentAgentId!,
            command.ChildLaunchJobId!,
            begun.Revision,
            command.ExpectedWorkDir,
            command.ExpectedRunnerId,
            command.ExpectedRuntime,
            command.ExpectedRuntimeSessionId,
            projectId,
            acquired.BindingEpoch,
            acquired.ReceiptId,
            SessionTreeExpectedLinkState.Absent));
        await fence.AcknowledgeFinalizeAsync(applied.Receipt!);
        Assert.Equal(LinkReservationState.Attached,
            (await fence.CommitFinalizeAsync(command.CommandId, command.EdgeId, begun.Revision)).State);
    }
}
