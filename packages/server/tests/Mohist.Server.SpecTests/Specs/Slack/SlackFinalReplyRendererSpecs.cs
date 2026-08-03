using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackFinalReplyRendererSpecs
{
    [Fact]
    public void FinalReply_IsConclusionFirst_Readable_AndBoundedToThreeResults()
    {
        var projection = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "complete the requested change",
            SlackFinalReplyStatus.PartiallyCompleted,
            Summary: "The confirmed work stopped after the available steps.",
            CompletedParts: ["implementation", "focused verification"],
            KeyResults: ["the remaining check needs attention", "this fourth item is not shown"],
            Actions: ["resolve the remaining check"],
            NextStep: "Resolve the remaining check and ask me to continue."));
        var text = string.Join('\n', projection.Segments);

        Assert.StartsWith("Conclusion: Partially completed - complete the requested change.", text, StringComparison.Ordinal);
        Assert.Contains("- Completed: implementation", text);
        Assert.Contains("- Completed: focused verification", text);
        Assert.Contains("- the remaining check needs attention", text);
        Assert.DoesNotContain("this fourth item is not shown", text);
        Assert.Contains("Actions:\n- resolve the remaining check", text);
        Assert.EndsWith("Next step: Resolve the remaining check and ask me to continue.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{", text);
        Assert.DoesNotContain("raw tool", text, StringComparison.OrdinalIgnoreCase);
    }
}
