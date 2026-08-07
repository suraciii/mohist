using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackMarkdownRendererTests
{
    [Theory]
    [InlineData("**重要**", "*重要*")]
    [InlineData("plain **bold** text", "plain *bold* text")]
    [InlineData("**a** and **b**", "*a* and *b*")]
    public void ToMrkdwn_ConvertsBoldDelimiters(string markdown, string expected)
    {
        Assert.Equal(expected, SlackMarkdownRenderer.ToMrkdwn(markdown));
    }

    [Fact]
    public void ToMrkdwn_LeavesInlineCodeAndItsContentUntouched()
    {
        var markdown = "run `**not bold**` then **bold**";

        var rendered = SlackMarkdownRenderer.ToMrkdwn(markdown);

        Assert.Equal("run `**not bold**` then *bold*", rendered);
    }

    [Fact]
    public void ToMrkdwn_PassesFencedCodeBlocksThroughVerbatim()
    {
        var markdown = "before\n```\n**not bold**\n- not a list\n```\nafter";

        var rendered = SlackMarkdownRenderer.ToMrkdwn(markdown);

        Assert.Equal("before\n```\n**not bold**\n- not a list\n```\nafter", rendered);
    }

    [Fact]
    public void ToMrkdwn_ConvertsListMarkersToBullets()
    {
        var markdown = "- first\n- second\n  - nested\n* star\n+ plus\n1. ordered";

        var rendered = SlackMarkdownRenderer.ToMrkdwn(markdown);

        Assert.Equal("• first\n• second\n  • nested\n• star\n• plus\n1. ordered", rendered);
    }

    [Fact]
    public void ToMrkdwn_KeepsQuotesAsSlackQuotes()
    {
        Assert.Equal("> quoted line", SlackMarkdownRenderer.ToMrkdwn("> quoted line"));
    }

    [Fact]
    public void ToMrkdwn_DegradesHeadingsToPlainText()
    {
        var markdown = "# Title\n## Sub title\nBody";

        var rendered = SlackMarkdownRenderer.ToMrkdwn(markdown);

        Assert.Equal("Title\nSub title\nBody", rendered);
    }

    [Fact]
    public void ToMrkdwn_DegradesTablesToReadablePlainText()
    {
        var markdown = "| A | B |\n|---|---|\n| 1 | 2 |";

        var rendered = SlackMarkdownRenderer.ToMrkdwn(markdown);

        Assert.Equal("A | B\n1 | 2", rendered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToMrkdwn_EmptyInputStaysEmpty(string? markdown)
    {
        Assert.Equal(string.Empty, SlackMarkdownRenderer.ToMrkdwn(markdown));
    }

    [Fact]
    public void ToMrkdwn_AlreadyMrkdwnTextIsUnchanged()
    {
        var mrkdwn = "*bold* and `code`\n• item\n> quote";

        Assert.Equal(mrkdwn, SlackMarkdownRenderer.ToMrkdwn(mrkdwn));
    }
}
