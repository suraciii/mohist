using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public sealed class AgentSessionRuntimeStampingTests
{
    private static readonly DateTime CreatedAt = new(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AttachPhysicalSession_InitialBindingHasNoLineageAndEmitsCurrentBinding()
    {
        var session = CreateSession("opencode");

        var events = session.AttachPhysicalSession("runtime-session-1", null, "/work", null, null, CreatedAt.AddMinutes(1));

        Assert.Equal("runtime-session-1", session.Status.AgentRuntimeSessionId);
        Assert.DoesNotContain("Lineage", JsonSerializer.Serialize(session));
        var bound = Assert.IsType<AgentSessionRuntimeBound>(Assert.Single(events).Value);
        Assert.Equal("opencode", bound.Runtime);
    }

    [Fact]
    public void AttachPhysicalSession_RuntimeChangeUsesIdleCasAndClearsContext()
    {
        var session = CreateSession("opencode");
        session.AttachPhysicalSession("oc-session", null, "/work", null, null, CreatedAt.AddMinutes(1));
        session.Status = session.Status with
        {
            UsageSummary = new AgentUsageSummary { TotalTokens = 10, ContextWindowUsed = 12, ContextWindowSize = 100 }
        };

        var events = session.AttachPhysicalSession(
            "pi-session",
            null,
            "/work",
            null,
            null,
            CreatedAt.AddMinutes(2),
            "pi",
            "opencode",
            "oc-session");

        Assert.Equal("pi", session.Runtime.Runtime);
        Assert.Null(session.Status.UsageSummary!.ContextWindowUsed);
        Assert.Equal(10, session.Status.UsageSummary.TotalTokens);
        Assert.Single(events, e => e.Value is AgentSessionRuntimeBound);
    }

    [Fact]
    public void LegacyStateWithRemovedLineageFieldDeserializes()
    {
        var session = CreateSession("opencode");
        session.AttachPhysicalSession("legacy-session", null, "/work", null, null, CreatedAt.AddMinutes(1));
        var state = JsonNode.Parse(JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions))!.AsObject();
        state["status"]!.AsObject()["runtimeSessionLineage"] = new JsonArray(new JsonObject { ["agentRuntimeSessionId"] = "old" });
        state["runtime"]!.AsObject().Remove("runtime");

        var rehydrated = JsonSerializer.Deserialize<AgentSession>(state, AgentSessionJson.JsonOptions)!;

        Assert.Equal("legacy-session", rehydrated.Status.AgentRuntimeSessionId);
        Assert.Null(rehydrated.Runtime.Runtime);
        Assert.DoesNotContain("Lineage", JsonSerializer.Serialize(rehydrated));
    }

    [Fact]
    public void AttachPhysicalSession_StaleExpectedBindingDoesNotMutate()
    {
        var session = CreateSession("opencode");
        session.AttachPhysicalSession("current", null, "/work", null, null, CreatedAt.AddMinutes(1));

        Assert.Throws<StaleRuntimeSessionBindingException>(() => session.AttachPhysicalSession(
            "replacement", null, "/work", null, null, CreatedAt.AddMinutes(2), "opencode", "opencode", "stale"));

        Assert.Equal("current", session.Status.AgentRuntimeSessionId);
    }

    private static AgentSession CreateSession(string? runtime) => AgentSession.Create(
        "session-1", "runner-1", "/work",
        metadata: new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "build"),
        now: CreatedAt, runtime: runtime);
}
