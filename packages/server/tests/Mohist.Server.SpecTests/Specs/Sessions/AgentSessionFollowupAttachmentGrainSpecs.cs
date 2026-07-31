using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed partial class AgentSessionFollowupGrainSpecs
{
    [Fact]
    public async Task AcceptFollowup_QueuedAttachmentInputDoesNotJoinAnotherInput()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-attachment-owning-turn");
        var attachment = new AgentSessionInputAttachmentDescriptor(
            "att-1",
            "notes.txt",
            "text/plain",
            5,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "",
            Source: "agent-session-followup",
            IdempotencyKey: "attachment-key",
            Attachments: new[] { attachment }));
        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "follow-up text",
            Source: "agent-session-followup",
            IdempotencyKey: "text-key"));

        Assert.NotEqual(first.TurnId, second.TurnId);
        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.All(state!.Status.Turns!, turn => Assert.Single(turn.InputIds));
    }
}
