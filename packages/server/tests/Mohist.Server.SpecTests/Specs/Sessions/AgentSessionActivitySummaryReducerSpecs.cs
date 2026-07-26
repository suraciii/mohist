using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class AgentSessionActivitySummaryReducerSpecs
{
    private static readonly DateTime ObservedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Reduce_WithoutApplicableObservations_ExposesEmptySummary()
    {
        var state = AgentSessionActivitySummaryReducer.Reduce(
            AgentSessionActivitySummaryState.Empty,
            [Part(TranscriptPartTypes.Input, "input", "{\"text\":\"hello\"}")]);

        Assert.Equal(AgentSessionTranscriptSummary.Empty, state.Summary);
    }

    [Fact]
    public void Reduce_ModelUpdates_UsesTheLatestResolvedModel()
    {
        var state = AgentSessionActivitySummaryReducer.Reduce(
            AgentSessionActivitySummaryState.Empty,
            [
                Part(TranscriptPartTypes.Model, "model", "{\"resolvedModel\":\"old\"}"),
                Part(TranscriptPartTypes.Model, "model", "{\"resolvedModel\":\"new\"}")
            ]);

        Assert.Equal("new", state.Summary.ResolvedModel);
    }

    [Fact]
    public void Reduce_SameTurnToolReplacement_UsesFinalToolState()
    {
        var state = AgentSessionActivitySummaryReducer.Reduce(
            AgentSessionActivitySummaryState.Empty,
            [
                Part(TranscriptPartTypes.Input, "input", "{}"),
                Part(TranscriptPartTypes.Tool, "tool-1", "{\"toolCallId\":\"tool-1\",\"status\":\"failed\"}"),
                Part(TranscriptPartTypes.Tool, "tool-1", "{\"toolCallId\":\"tool-1\",\"status\":\"completed\"}")
            ]);

        Assert.Equal(1, state.Summary.ToolCallCount);
        Assert.Null(state.Summary.ToolErrorCount);
    }

    [Fact]
    public void Reduce_SealedToolFailure_RemainsAfterLaterTurnCompletesSameIdentifier()
    {
        var state = AgentSessionActivitySummaryReducer.Reduce(
            AgentSessionActivitySummaryState.Empty,
            [
                Part(TranscriptPartTypes.Input, "input-1", "{}"),
                Part(TranscriptPartTypes.Tool, "tool-1", "{\"toolCallId\":\"tool-1\",\"status\":\"failed\"}"),
                Part(TranscriptPartTypes.Input, "input-2", "{}"),
                Part(TranscriptPartTypes.Tool, "tool-1", "{\"toolCallId\":\"tool-1\",\"status\":\"completed\"}")
            ]);

        Assert.Equal(1, state.Summary.ToolCallCount);
        Assert.Equal(1, state.Summary.ToolErrorCount);
    }

    [Fact]
    public void Reduce_SessionActivityCandidates_UseTurnPartAndIdentifierOrderAndClearAsAPair()
    {
        var state = AgentSessionActivitySummaryReducer.Reduce(
            AgentSessionActivitySummaryState.Empty,
            [
                Part(TranscriptPartTypes.Input, "input-1", "{}"),
                Part(TranscriptPartTypes.SessionActivity, "activity", "{\"failureCategory\":\"first\",\"failureReason\":\"first reason\"}"),
                Part(TranscriptPartTypes.SessionActivity, "activity", "{\"failureReason\":\"second reason\"}"),
                Part(TranscriptPartTypes.Input, "input-2", "{}"),
                Part(TranscriptPartTypes.SessionActivity, "activity", "{}")
            ]);

        Assert.Null(state.Summary.FailureCategory);
        Assert.Null(state.Summary.FailureReason);
        Assert.Equal(2, state.LatestActivity!.TurnSequence);
        Assert.Equal(1, state.LatestActivity.PartSequence);
    }

    private static AgentSessionTranscriptPartDelta Part(string type, string key, string payload) =>
        new(type, key, null, null, payload, ObservedAt, ObservedAt, 1);
}
