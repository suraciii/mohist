using Mohist.Server.Api;
using Mohist.Server.Slack;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Slack;

/// <summary>
/// Component Specs for the New task leading marker detector
/// on <see cref="SlackConnectionRoutes"/>. Companion to the route Specs in
/// <c>SlackDmNewTaskIngressSpecs</c>, which exercise the
/// full ingress path with a fake Slack ingress. This file pins down the
/// case-insensitive + standalone-token rule in isolation so a future
/// tweak to <see cref="SlackConnectionRoutes.TryStripNewTaskMarker"/>
/// can't drift from the product spec without a red test.
/// </summary>
public sealed class SlackDmNewTaskSpecs
{
    [Theory]
    [InlineData("new task do something", "do something")]
    [InlineData("new task   leading whitespace", "leading whitespace")]
    [InlineData("New Task budget review", "budget review")]
    [InlineData("NEW TASK alpha", "alpha")]
    [InlineData("   new task tabs before", "tabs before")]
    [InlineData("new task\twith\ttabs", "with\ttabs")]
    [InlineData("new task", "")]
    [InlineData("new task   ", "")]
    public void TryStripNewTaskMarker_MatchesLeadingMarkerAndStripsIt(string input, string expectedRemaining)
    {
        var matched = SlackConnectionRoutes.TryStripNewTaskMarker(input, out var remaining);

        Assert.True(matched);
        Assert.Equal(expectedRemaining, remaining);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("new")]
    [InlineData("new tasks foo")]
    [InlineData("new tasking for monday")]
    [InlineData("a new task for later")]
    [InlineData("continue the new task")]
    [InlineData("nEWtask foo")]
    [InlineData("Hello world")]
    public void TryStripNewTaskMarker_DoesNotMatchWhenMarkerIsNotLeadingStandaloneToken(string input)
    {
        var matched = SlackConnectionRoutes.TryStripNewTaskMarker(input, out var remaining);

        Assert.False(matched);
        Assert.Equal(string.Empty, remaining);
    }

    [Theory]
    [InlineData("", false, "", 0, "Please send a task for the Agent to perform.")]
    [InlineData("new task", true, "", 0, "Please send a task for the Agent to perform.")]
    [InlineData("new task", true, "", 1, null)]
    [InlineData("task", false, "", 0, null)]
    [InlineData("new task work", true, "work", 0, null)]
    public void Empty_task_rejection_requires_text_or_an_attachment(
        string prompt,
        bool isNewTask,
        string newTaskPrompt,
        int attachmentCount,
        string? expectedReason)
    {
        Assert.Equal(expectedReason,
            SlackDmIngressPolicy.EmptyTaskRejectionReason(
                prompt, isNewTask, newTaskPrompt, attachmentCount));
    }

    [Fact]
    public void TryStripNewTaskMarker_TreatsTheMarkerConstantAsTheCanonicalForm()
    {
        Assert.Equal("new task", SlackConnectionRoutes.NewTaskMarker);
    }

    [Theory]
    [InlineData(false, null, "Starting a new task. Task accepted and queued for execution.")]
    [InlineData(false, "Agent executability is unknown; the task is accepted and awaiting Runner verification.",
        "Starting a new task. Agent executability is unknown; the task is accepted and awaiting Runner verification.")]
    [InlineData(true, "Agent executability is unknown; the task is accepted and awaiting Runner verification.",
        "This new task was already accepted; execution is being resumed.")]
    [InlineData(true, "", "This new task was already accepted; execution is being resumed.")]
    public void BuildNewTaskAck_PrefixesTheStartingANewTaskSignalAndDistinguishesRedeliveries(
        bool inboxAlreadyExisted,
        string? dispatchDecisionReason,
        string expected)
    {
        var actual = SlackConnectionRoutes.BuildNewTaskAck(inboxAlreadyExisted, dispatchDecisionReason);

        Assert.Equal(expected, actual);
    }
}
