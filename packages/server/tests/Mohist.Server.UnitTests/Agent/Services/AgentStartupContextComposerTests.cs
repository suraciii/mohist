using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class AgentStartupContextComposerTests
{
    [Fact]
    public void ComposePrompt_NullContext_ReturnsTaskPromptUnchanged()
    {
        const string prompt = "summarize the diff";

        Assert.Equal(prompt, AgentStartupContextComposer.ComposePrompt(prompt, null));
    }

    [Fact]
    public void ComposePrompt_WithContext_PrependsReadOnlyBackground()
    {
        var prompt = "summarize the diff";
        var context = new AgentStartupContext(
            Text: "alice: should we ship?\nbob: yes",
            Provenance: new AgentStartupContextProvenance(
                Source: "slack-thread-history",
                Truncated: false,
                TruncationMarker: null,
                OmittedOldestMessageCount: 0));

        var composed = AgentStartupContextComposer.ComposePrompt(prompt, context);

        Assert.StartsWith(AgentStartupContextComposer.BackgroundHeader, composed, StringComparison.Ordinal);
        Assert.Contains("alice: should we ship?\nbob: yes", composed, StringComparison.Ordinal);
        Assert.EndsWith(prompt, composed, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposePrompt_WithTruncatedContext_PrependsTruncationMarker()
    {
        var prompt = "review the change";
        var context = new AgentStartupContext(
            Text: "newest message",
            Provenance: new AgentStartupContextProvenance(
                Source: "slack-thread-history",
                Truncated: true,
                TruncationMarker: "10 oldest messages omitted",
                OmittedOldestMessageCount: 10));

        var composed = AgentStartupContextComposer.ComposePrompt(prompt, context);

        Assert.Contains("10 oldest messages omitted", composed, StringComparison.Ordinal);
        Assert.Contains(AgentStartupContextComposer.BackgroundHeader, composed, StringComparison.Ordinal);
        Assert.Contains("newest message", composed, StringComparison.Ordinal);
        Assert.EndsWith(prompt, composed, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBackground_TruncationMarker_PrecedesBackgroundBody()
    {
        var context = new AgentStartupContext(
            Text: "body text",
            Provenance: new AgentStartupContextProvenance(
                Source: "slack-thread-history",
                Truncated: true,
                TruncationMarker: "5 oldest messages omitted",
                OmittedOldestMessageCount: 5));

        var rendered = AgentStartupContextComposer.RenderBackground(context);

        var markerIndex = rendered.IndexOf("5 oldest messages omitted", StringComparison.Ordinal);
        var bodyIndex = rendered.IndexOf("body text", StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        Assert.True(bodyIndex > markerIndex, "Truncation marker must precede the background body.");
        Assert.StartsWith(AgentStartupContextComposer.BackgroundHeader, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposePrompt_ContextDoesNotIncludeNewlinesWhenTaskPromptContainsLeadingNewlines()
    {
        var prompt = "review the change";
        var context = new AgentStartupContext(
            Text: "discussion body",
            Provenance: new AgentStartupContextProvenance(
                Source: "slack-thread-history",
                Truncated: false,
                TruncationMarker: null,
                OmittedOldestMessageCount: 0));

        var composed = AgentStartupContextComposer.ComposePrompt(prompt, context);

        Assert.Equal(
            $"{AgentStartupContextComposer.BackgroundHeader}\ndiscussion body\n\n{prompt}",
            composed);
    }
}