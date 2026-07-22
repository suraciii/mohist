using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public class TranscriptEventSummaryProjectorTests
{
    [Fact]
    public void Summarize_ModelPartWithResolvedModel_ExposesResolvedModel()
    {
        var events = new[]
        {
            ModelPart(turnSequence: 1, sequence: 1, partId: "model-1", payload: new { resolvedModel = "anthropic/claude-sonnet-4-20250514" }),
        };

        var summary = TranscriptEventSummaryProjector.Summarize(events);

        Assert.Equal("anthropic/claude-sonnet-4-20250514", summary.ResolvedModel);
    }

    [Fact]
    public void Summarize_ModelPartWithoutResolvedField_LeavesResolvedModelNull()
    {
        var events = new[]
        {
            ModelPart(turnSequence: 1, sequence: 1, partId: "model-1", payload: new { model = "anthropic/claude-sonnet-4-20250514" }),
        };

        var summary = TranscriptEventSummaryProjector.Summarize(events);

        Assert.Null(summary.ResolvedModel);
    }

    [Fact]
    public void Summarize_MultipleModelParts_LastResolvedModelWins()
    {
        var events = new[]
        {
            ModelPart(turnSequence: 1, sequence: 1, partId: "model-1", payload: new { resolvedModel = "anthropic/claude-sonnet-3" }),
            ModelPart(turnSequence: 1, sequence: 2, partId: "model-2", payload: new { resolvedModel = "anthropic/claude-sonnet-4" }),
        };

        var summary = TranscriptEventSummaryProjector.Summarize(events);

        Assert.Equal("anthropic/claude-sonnet-4", summary.ResolvedModel);
    }

    [Fact]
    public void Summarize_NoModelParts_LeavesResolvedModelNull()
    {
        var events = new[]
        {
            new TranscriptSummaryEvent(1, 1, "tool-1", TranscriptPartTypes.Tool, JsonSerializer.Serialize(new { toolCallId = "tool-1", status = "completed" })),
        };

        var summary = TranscriptEventSummaryProjector.Summarize(events);

        Assert.Null(summary.ResolvedModel);
    }

    [Fact]
    public void Summarize_ResolvedModelMatchesDomainApplyRuntimeEvent_ForSamePayload()
    {
        var payload = new { resolvedModel = "openai/gpt-5.6" };
        var payloadJson = JsonSerializer.Serialize(payload, JSON.Options);

        var projector = TranscriptEventSummaryProjector.Summarize(new[]
        {
            ModelPart(turnSequence: 1, sequence: 1, partId: "model-1", payload: payload),
        });

        var session = BuildSession();
        var now = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);
        session.ApplyRuntimeEventModelResolved(payloadJson, now);

        Assert.Equal(projector.ResolvedModel, session.Settings.Model);
        Assert.Equal("openai/gpt-5.6", projector.ResolvedModel);
        Assert.Equal("openai/gpt-5.6", session.Settings.Model);
    }

    [Fact]
    public void Summarize_ResolvedModelIgnoresDivergentModelField_WhileGrainReadsResolvedModelOnly()
    {
        var projectorPayload = JsonSerializer.Serialize(new { model = "legacy/divergent" }, JSON.Options);
        var grainPayload = JsonSerializer.Serialize(new { resolvedModel = "openai/gpt-5.6" }, JSON.Options);

        var summary = TranscriptEventSummaryProjector.Summarize(new[]
        {
            ModelPart(turnSequence: 1, sequence: 1, partId: "model-1", payloadJson: projectorPayload),
        });

        var session = BuildSession();
        var now = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);
        session.ApplyRuntimeEventModelResolved(grainPayload, now);

        Assert.Null(summary.ResolvedModel);
        Assert.Equal("openai/gpt-5.6", session.Settings.Model);
    }

    private static TranscriptSummaryEvent ModelPart(long turnSequence, long sequence, string partId, object payload) =>
        new(turnSequence, sequence, partId, TranscriptPartTypes.Model, JsonSerializer.Serialize(payload, JSON.Options));

    private static TranscriptSummaryEvent ModelPart(long turnSequence, long sequence, string partId, string payloadJson) =>
        new(turnSequence, sequence, partId, TranscriptPartTypes.Model, payloadJson);

    private static AgentSession BuildSession()
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "proj")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "wf")
            .WithLabel("mohist.io/session-name", "session");

        var session = AgentSession.Create(
            "proj/wf/session",
            "runner-1",
            "/work",
            metadata: metadata,
            now: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        session.Settings = new AgentSessionSettings("opencode");
        return session;
    }
}

internal static class TranscriptSummaryTestSessionExtensions
{
    public static void ApplyRuntimeEventModelResolved(this AgentSession session, string payloadJson, DateTime now)
    {
        var payload = AgentSessionJsonHelper.ParsePayload(payloadJson);
        var resolvedModel = AgentSessionJsonHelper.GetStringProp(payload, "resolvedModel");
        session.ResolveModel(resolvedModel, now);
    }
}
