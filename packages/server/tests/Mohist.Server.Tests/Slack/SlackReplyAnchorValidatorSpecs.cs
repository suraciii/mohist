using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class SlackReplyAnchorValidatorSpecs
{
    private static readonly DateTime FixedNow = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Initial_direct_message_uses_its_own_triggering_message_as_the_reply_root()
    {
        var session = SessionWithTurns(
            InitialInput("initial-input", "initial-message", threadId: null, boundRoot: "initial-message"),
            new AgentTurnRecord("initial-turn", 1, ["initial-input"], AgentTurnStatus.Executing, JobId: "job-1"));
        var request = Request(session, "initial-message", "initial-message", "slack:session-1:initial-input");

        Assert.Equal(new SlackReplyAnchorValidationResult(true, true),
            SlackReplyAnchorValidator.Validate(session, request));
    }

    [Fact]
    public void Threaded_followup_requires_its_durable_bound_root_instead_of_legacy_thread_fallback()
    {
        var session = SessionWithTurns(
            InitialInput("initial-input", "root-message", threadId: null, boundRoot: null),
            new AgentTurnRecord("initial-turn", 1, ["initial-input"], AgentTurnStatus.Completed, JobId: "job-1"),
            new AgentSessionInputRecord(
                "followup-input",
                2,
                "continue",
                "agent-session-followup",
                AgentSessionInputAcceptance.Accepted,
                FixedNow,
                Provenance: Provenance("followup-message", "root-message", boundRoot: "root-message")),
            new AgentTurnRecord(
                "followup-turn",
                2,
                ["followup-input"],
                AgentTurnStatus.Executing,
                OperationId: "operation-1"));
        var request = Request(session, "root-message", "followup-message", "operation-1");

        Assert.Equal(new SlackReplyAnchorValidationResult(true, true),
            SlackReplyAnchorValidator.Validate(session, request));

        Assert.False(SlackReplyAnchorValidator.Validate(
            session,
            request with { ThreadRootMessageId = "legacy-thread-root" }).Valid);
    }

    [Fact]
    public void Threaded_current_input_uses_its_own_durable_bound_root()
    {
        var session = SessionWithTurns(
            InitialInput("initial-input", "initial-message", threadId: null, boundRoot: "initial-message"),
            new AgentTurnRecord("initial-turn", 1, ["initial-input"], AgentTurnStatus.Completed, JobId: "job-1"),
            new AgentSessionInputRecord(
                "followup-input",
                2,
                "continue",
                "agent-session-followup",
                AgentSessionInputAcceptance.Accepted,
                FixedNow,
                Provenance: Provenance("followup-message", "legacy-thread", boundRoot: "current-root")),
            new AgentTurnRecord(
                "followup-turn",
                2,
                ["followup-input"],
                AgentTurnStatus.Executing,
                OperationId: "operation-1"));
        var request = Request(session, "current-root", "followup-message", "operation-1");

        Assert.Equal(new SlackReplyAnchorValidationResult(true, true),
            SlackReplyAnchorValidator.Validate(session, request));
    }

    [Fact]
    public void Anchor_validation_binds_project_connection_conversation_and_trigger_identity()
    {
        var session = SessionWithTurns(
            InitialInput("initial-input", "message-1", threadId: null, boundRoot: "message-1"),
            new AgentTurnRecord("initial-turn", 1, ["initial-input"], AgentTurnStatus.Executing, JobId: "job-1"));
        var request = Request(session, "message-1", "message-1", "slack:session-1:initial-input");

        Assert.False(SlackReplyAnchorValidator.Validate(session, request with { ProjectId = "other-project" }).Valid);
        Assert.False(SlackReplyAnchorValidator.Validate(session, request with { ConnectionId = "other-connection" }).Valid);
        Assert.False(SlackReplyAnchorValidator.Validate(session, request with { ConversationId = "other-conversation" }).Valid);
        Assert.False(SlackReplyAnchorValidator.Validate(session, request with { TriggeringMessageId = "other-message" }).Valid);
        Assert.False(SlackReplyAnchorValidator.Validate(session, request with { DispatchRef = "other-dispatch" }).Valid);
    }

    private static AgentSession SessionWithTurns(params object[] records)
    {
        var inputs = records.OfType<AgentSessionInputRecord>().ToArray();
        var turns = records.OfType<AgentTurnRecord>().ToArray();
        return new AgentSession
        {
            Id = "session-1",
            Metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = "project-1",
            }),
            Runtime = new AgentSessionRuntime("runner-1", "/work", "opencode"),
            Status = AgentSessionStatusSnapshot.Created(FixedNow) with
            {
                Inputs = inputs,
                Turns = turns,
            },
        };
    }

    private static AgentSessionInputRecord InitialInput(
        string inputId,
        string messageId,
        string? threadId,
        string? boundRoot) =>
        new(
            inputId,
            1,
            "start",
            "agent-launch",
            AgentSessionInputAcceptance.Accepted,
            FixedNow,
            JobId: "job-1",
            Provenance: Provenance(messageId, threadId, boundRoot));

    private static AgentSessionInputProvenance Provenance(
        string messageId,
        string? threadId,
        string? boundRoot) =>
        new(
            "slack",
            "T1",
            "C1",
            threadId,
            "U1",
            messageId,
            "connection-1",
            boundRoot);

    private static SlackReplyAnchorValidationRequest Request(
        AgentSession session,
        string root,
        string triggering,
        string dispatchRef) =>
        new(
            "project-1",
            "T1",
            "C1",
            root,
            triggering,
            "connection-1",
            session.Id,
            dispatchRef);
}
