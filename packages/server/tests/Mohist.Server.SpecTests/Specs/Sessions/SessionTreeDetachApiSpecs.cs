using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class SessionTreeDetachApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SessionTreeDetachApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AttachedChildDetachReplayReturnsTheSameHistoricTuple()
    {
        var projectId = await CreateProjectAsync("detach-api");
        var setup = await AttachChildAsync(projectId, "historic");
        var path = $"/api/projects/{projectId}/agent-sessions/{setup.ChildId}/detach";

        var first = await PostDetachAsync(path);
        Assert.Equal("detached", first.GetProperty("state").GetString());
        Assert.False(first.GetProperty("historic").GetBoolean());
        Assert.Equal(setup.ParentId, first.GetProperty("parentSessionId").GetString());
        Assert.Equal(setup.EdgeId, first.GetProperty("edgeId").GetString());
        Assert.Equal(setup.JobId, first.GetProperty("childLaunchJobId").GetString());

        var replay = await PostDetachAsync(path);
        Assert.True(replay.GetProperty("historic").GetBoolean());
        Assert.Equal(first.GetProperty("childSessionId").GetString(), replay.GetProperty("childSessionId").GetString());
        Assert.Equal(first.GetProperty("parentSessionId").GetString(), replay.GetProperty("parentSessionId").GetString());
        Assert.Equal(first.GetProperty("edgeId").GetString(), replay.GetProperty("edgeId").GetString());
        Assert.Equal(first.GetProperty("childLaunchJobId").GetString(), replay.GetProperty("childLaunchJobId").GetString());
        Assert.Equal(first.GetProperty("attachedRevision").GetInt64(), replay.GetProperty("attachedRevision").GetInt64());
        Assert.Equal(first.GetProperty("detachedRevision").GetInt64(), replay.GetProperty("detachedRevision").GetInt64());
    }

    [Fact]
    public async Task DetachUnattachedAndCrossProjectChildFailClosed()
    {
        var projectId = await CreateProjectAsync("detach-unattached");
        var childId = $"detach-unattached-child-{Guid.NewGuid():N}";
        await OpenSessionAsync(projectId, childId, "detach-unattached");

        using var unattached = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/agent-sessions/{childId}/detach", null);
        Assert.Equal(HttpStatusCode.NotFound, unattached.StatusCode);

        var otherProjectId = await CreateProjectAsync("detach-cross-project");
        using var crossProject = await _fixture.Client.PostAsync(
            $"/api/projects/{otherProjectId}/agent-sessions/{childId}/detach", null);
        Assert.Equal(HttpStatusCode.NotFound, crossProject.StatusCode);
    }

    [Fact]
    public async Task DetachRevisionMismatchDoesNotAdvanceTheFence()
    {
        var projectId = $"detach-mismatch-{Guid.NewGuid():N}";
        var setup = await AttachChildAsync(projectId, "mismatch");
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var mismatch = await fence.BeginDetachAsync(new BeginSessionTreeDetachCommand(
            projectId,
            setup.EdgeId,
            setup.ParentId,
            setup.ChildId,
            "detach-wrong-revision",
            setup.JobId,
            setup.AttachedRevision + 1));

        Assert.Equal(SessionTreeDetachMutationState.ReconciliationRequired, mismatch.State);
        Assert.Equal(setup.AttachedRevision, (await fence.GetAsync()).GraphRevision);
        Assert.Equal(
            LinkReservationState.Attached,
            (await fence.GetAsync()).Reservations!.Single(item => item.EdgeId == setup.EdgeId).State);
    }

    private async Task<JsonElement> PostDetachAsync(string path)
    {
        using var response = await _fixture.Client.PostAsync(path, null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data");
    }

    private async Task<(string ParentId, string ChildId, string EdgeId, string JobId, long AttachedRevision)> AttachChildAsync(
        string projectId,
        string suffix)
    {
        var parentId = $"detach-parent-{suffix}-{Guid.NewGuid():N}";
        var childId = $"detach-child-{suffix}-{Guid.NewGuid():N}";
        var edgeId = $"detach-edge-{suffix}-{Guid.NewGuid():N}";
        var jobId = $"detach-job-{suffix}-{Guid.NewGuid():N}";
        await OpenSessionAsync(projectId, parentId, "detach-parent");
        var child = await OpenSessionAsync(projectId, childId, "detach-child");
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var command = new ReserveSessionTreeLinkCommand(
            projectId,
            edgeId,
            parentId,
            childId,
            "/workspace",
            "runner-detach-api",
            "opencode",
            "runtime-detach-api",
            $"detach-command-{suffix}-{Guid.NewGuid():N}",
            jobId,
            "detach-parent",
            1,
            SessionTreeExpectedLinkState.Absent);
        await fence.ReserveAsync(command);
        var binding = (await _fixture.Grains.GetGrain<IAgentSessionGrain>(parentId)
            .AcquireChildAttachBindingAsync(new AcquireChildAttachBindingCommand(
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
        var begun = await fence.BeginFinalizeAsync(command.CommandId, command.EdgeId, binding);
        var applied = await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            command.CommandId,
            command.EdgeId,
            parentId,
            command.ParentAgentId!,
            jobId,
            begun.Revision,
            command.ExpectedWorkDir,
            command.ExpectedRunnerId,
            command.ExpectedRuntime,
            command.ExpectedRuntimeSessionId,
            projectId,
            binding.BindingEpoch,
            binding.ReceiptId,
            SessionTreeExpectedLinkState.Absent));
        await fence.AcknowledgeFinalizeAsync(applied.Receipt!);
        Assert.Equal(LinkReservationState.Attached,
            (await fence.CommitFinalizeAsync(command.CommandId, command.EdgeId, begun.Revision)).State);
        return (parentId, childId, edgeId, jobId, begun.Revision);
    }

    private async Task<IAgentSessionGrain> OpenSessionAsync(
        string projectId,
        string sessionId,
        string agentId)
    {
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            "runner-detach-api",
            "opencode",
            "/workspace",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = agentId,
            })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "runtime-detach-api",
            ExpectedRunnerId: "runner-detach-api",
            ExpectedRuntime: "opencode"));
        return session;
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
}
