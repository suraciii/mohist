using Mohist.Server.Api;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackAttachmentInputBinderTests
{
    [Fact]
    public void DeterministicAttachmentIdIsStableAndOpaque()
    {
        var identity = new SlackMessageIdentity("T1", "D1", "123.456");

        var first = SlackAttachmentInputBinder.DeterministicAttachmentId(identity, "F1");
        var replay = SlackAttachmentInputBinder.DeterministicAttachmentId(identity, "F1");
        var otherMessage = SlackAttachmentInputBinder.DeterministicAttachmentId(
            identity with { MessageTs = "123.457" }, "F1");

        Assert.Equal(first, replay);
        Assert.NotEqual(first, otherMessage);
        Assert.StartsWith("att_", first);
        Assert.DoesNotContain("F1", first);
    }

    [Fact]
    public void BuildAttachmentAckNamesAcceptedAndRejectedFiles()
    {
        var files = new[]
        {
            new SlackIngressFile("F1", "report.txt", "text/plain", 10),
            new SlackIngressFile("F2", "video.mp4", "video/mp4", 20),
        };
        var binding = new SlackAttachmentBinding(
        [
            new AgentInputAttachmentAcceptance(
                "att_one",
                new AgentSessionInputAttachmentDescriptor(
                    "att_one", "report.txt", "text/plain", 10,
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "slack"),
                null,
                null),
            new AgentInputAttachmentAcceptance(
                "att_two",
                null,
                AgentInputAttachmentRejectionReason.UnsupportedType,
                "Attachment content-type 'video/mp4' is not supported."),
        ]);

        var reply = SlackConnectionRoutes.BuildAttachmentAck("Task accepted.", files, binding);

        Assert.Contains("Files received: report.txt.", reply);
        Assert.Contains("Files not used: video.mp4 (UnsupportedType:", reply);
        Assert.DoesNotContain("F1", reply);
        Assert.DoesNotContain("F2", reply);
    }
}
