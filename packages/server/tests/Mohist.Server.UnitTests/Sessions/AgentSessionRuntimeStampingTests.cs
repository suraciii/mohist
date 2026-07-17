using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public class AgentSessionRuntimeStampingTests
{
    private static readonly DateTime CreatedAt = new(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_StampsNormalizedRuntime()
    {
        var session = CreateSession(" opencode ");

        Assert.Equal("opencode", session.Runtime.Runtime);
    }

    [Fact]
    public void AttachPhysicalSession_RecordsRuntimeInLineageAndEvent()
    {
        var session = CreateSession("opencode");
        var boundAt = CreatedAt.AddMinutes(1);

        var events = session.AttachPhysicalSession(
            "runtime-session-1",
            "model-a",
            "/work",
            changeDir: null,
            processPid: null,
            boundAt);

        var lineage = Assert.Single(session.Status.RuntimeSessionLineage!);
        Assert.Equal("opencode", lineage.Runtime);
        var runtimeBound = Assert.IsType<AgentSessionRuntimeBound>(
            Assert.Single(events, candidate => candidate.Value is AgentSessionRuntimeBound).Value);
        Assert.Equal("opencode", runtimeBound.Runtime);
    }

    [Fact]
    public void RebindRuntimeSession_AppendsRuntimeWithoutChangingPriorLineage()
    {
        var session = CreateSession("opencode");
        var firstBoundAt = CreatedAt.AddMinutes(1);
        session.AttachPhysicalSession("runtime-session-1", null, "/work", null, null, firstBoundAt);

        var events = session.RebindRuntimeSession(
            "runtime-session-2",
            contextWindowUsedAfter: null,
            contextWindowSizeAfter: null,
            now: firstBoundAt.AddMinutes(1),
            runtime: "replacement-runtime");

        Assert.Equal("replacement-runtime", session.Runtime.Runtime);
        Assert.Collection(
            session.Status.RuntimeSessionLineage!,
            entry =>
            {
                Assert.Equal("runtime-session-1", entry.AgentRuntimeSessionId);
                Assert.Equal("opencode", entry.Runtime);
            },
            entry =>
            {
                Assert.Equal("runtime-session-2", entry.AgentRuntimeSessionId);
                Assert.Equal("replacement-runtime", entry.Runtime);
            });
        var runtimeBound = Assert.IsType<AgentSessionRuntimeBound>(Assert.Single(events).Value);
        Assert.Equal("replacement-runtime", runtimeBound.Runtime);
    }

    [Fact]
    public void RebindRuntimeSession_LegacyBindingKeepsUnknownPredecessorRuntime()
    {
        var session = CreateSession(runtime: null);
        session.Status = session.Status with
        {
            AgentRuntimeSessionId = "legacy-session",
            BoundAt = CreatedAt,
            RuntimeSessionLineage = null
        };

        session.RebindRuntimeSession(
            "runtime-session-2",
            contextWindowUsedAfter: null,
            contextWindowSizeAfter: null,
            now: CreatedAt.AddMinutes(1),
            runtime: "opencode");

        Assert.Collection(
            session.Status.RuntimeSessionLineage!,
            entry => Assert.Null(entry.Runtime),
            entry => Assert.Equal("opencode", entry.Runtime));
    }

    [Fact]
    public void LegacyStateWithoutRuntime_DeserializesWithoutBackfill()
    {
        var session = CreateSession("opencode");
        session.AttachPhysicalSession("legacy-session", null, "/work", null, null, CreatedAt.AddMinutes(1));
        var state = JsonNode.Parse(JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions))!.AsObject();
        state["runtime"]!.AsObject().Remove("runtime");
        state["status"]!["runtimeSessionLineage"]![0]!.AsObject().Remove("runtime");

        var rehydrated = JsonSerializer.Deserialize<AgentSession>(state, AgentSessionJson.JsonOptions)!;

        Assert.Equal(session.Id, rehydrated.Id);
        Assert.Null(rehydrated.Runtime.Runtime);
        Assert.Null(Assert.Single(rehydrated.Status.RuntimeSessionLineage!).Runtime);
    }

    [Fact]
    public void IsRuntimeSessionMissing_RequiresBindingRuntimeAndRegisteredBackend()
    {
        static bool IsRegistered(string runtime) => runtime == "opencode";

        var absentBinding = CreateSession("opencode");
        var absentRuntime = CreateSession(runtime: null);
        absentRuntime.Status = absentRuntime.Status with { AgentRuntimeSessionId = "legacy-session" };
        var unavailableRuntime = CreateSession("acp");
        unavailableRuntime.Status = unavailableRuntime.Status with { AgentRuntimeSessionId = "acp-session" };
        var availableRuntime = CreateSession("opencode");
        availableRuntime.Status = availableRuntime.Status with { AgentRuntimeSessionId = "opencode-session" };

        Assert.True(absentBinding.IsRuntimeSessionMissing(IsRegistered));
        Assert.True(absentRuntime.IsRuntimeSessionMissing(IsRegistered));
        Assert.True(unavailableRuntime.IsRuntimeSessionMissing(IsRegistered));
        Assert.False(availableRuntime.IsRuntimeSessionMissing(IsRegistered));
    }

    private static AgentSession CreateSession(string? runtime) =>
        AgentSession.Create(
            "session-1",
            "runner-1",
            "/work",
            metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", "project-1")
                .WithLabel("mohist.io/source-kind", "workflow")
                .WithLabel("mohist.io/source-id", "workflow-1")
                .WithLabel("mohist.io/session-name", "build"),
            now: CreatedAt,
            runtime: runtime);
}
