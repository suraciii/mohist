using Mohist.Server.Events.Subscriptions;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

/// <summary>
/// issue-490 T-002 — unit coverage for the mention token parser (design D4,
/// spec <i>Token parsing</i>). The parser is a pure function over the comment
/// body; the handler's behavior on top of it (loop prevention, resolution,
/// launch) is covered by <c>CommentMentionDispatchSpecs</c>.
/// </summary>
public class MentionTokenParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("no mentions here")]
    [InlineData("foo@bar embedded at-sign is not a mention")]
    [InlineData("plain text with punctuation, but no at-sign.")]
    public void Parse_NoMentions_ReturnsEmpty(string body)
    {
        var tokens = MentionTokenParser.Parse(body);
        Assert.Empty(tokens);
    }

    [Fact]
    public void Parse_NullBody_ReturnsEmpty()
    {
        Assert.Empty(MentionTokenParser.Parse(null));
    }

    [Fact]
    public void Parse_SingleLeadingMention_ReturnsOneToken()
    {
        var tokens = MentionTokenParser.Parse("@supervisor please help");
        var token = Assert.Single(tokens);
        Assert.Equal("supervisor", token);
    }

    [Theory]
    [InlineData("@supervisor, please help.", "supervisor")]
    [InlineData("ping @supervisor.", "supervisor")]
    [InlineData("hello @supervisor!", "supervisor")]
    public void Parse_TrailingPunctuationDoesNotBelongToToken(string body, string expected)
    {
        var tokens = MentionTokenParser.Parse(body);
        Assert.Contains(expected, tokens);
    }

    [Fact]
    public void Parse_AtSignInEmailOrUrl_IsNotAMention()
    {
        // The boundary prefix requires start-of-string or whitespace/
        // punctuation before the @. 'foo@bar' has 'o' before the @, which
        // is neither, so neither @-occurrence is a mention.
        Assert.Empty(MentionTokenParser.Parse("foo@bar is not a mention"));
        Assert.Empty(MentionTokenParser.Parse("reach me at ada@example.test"));
    }

    [Fact]
    public void Parse_AtSignAtStartOfWord_IsAMention()
    {
        var tokens = MentionTokenParser.Parse("@supervisor");
        var token = Assert.Single(tokens);
        Assert.Equal("supervisor", token);
    }

    [Fact]
    public void Parse_TrailingPeriodIsDelimiter()
    {
        // End-of-sentence period: '@supervisor.' parses as 'supervisor',
        // not 'supervisor.'.
        var tokens = MentionTokenParser.Parse("ping @supervisor.");
        var token = Assert.Single(tokens);
        Assert.Equal("supervisor", token);
    }

    [Fact]
    public void Parse_DotInsideNameStaysInToken()
    {
        var tokens = MentionTokenParser.Parse("ping @supervisor.io please");
        var token = Assert.Single(tokens);
        Assert.Equal("supervisor.io", token);
    }

    [Fact]
    public void Parse_HyphenInsideNameStaysInToken()
    {
        var tokens = MentionTokenParser.Parse("ping @principal-reviewer please");
        var token = Assert.Single(tokens);
        Assert.Equal("principal-reviewer", token);
    }

    [Fact]
    public void Parse_TrailingHyphenIsDelimiter()
    {
        var tokens = MentionTokenParser.Parse("ping @supervisor - other");
        var token = Assert.Single(tokens);
        Assert.Equal("supervisor", token);
    }

    [Fact]
    public void Parse_MultipleDistinctMentions_ReturnsAllInOrder()
    {
        var tokens = MentionTokenParser.Parse("@supervisor and @coder please");
        Assert.Equal(new[] { "supervisor", "coder" }, tokens);
    }

    [Fact]
    public void Parse_RepeatedMention_DedupesCaseInsensitively()
    {
        var tokens = MentionTokenParser.Parse("@supervisor @SuperVisor @SUPERVISOR");
        var token = Assert.Single(tokens);
        Assert.Equal("supervisor", token);
    }

    [Fact]
    public void Parse_CaseInsensitiveDedup_PreservesFirstOccurrenceCasing()
    {
        // The parser dedupes case-insensitively but preserves the first
        // occurrence's casing for downstream resolution + logging. The
        // handler resolves case-insensitively, so this is purely cosmetic.
        var tokens = MentionTokenParser.Parse("@SuperVisor @supervisor");
        var token = Assert.Single(tokens);
        Assert.Equal("SuperVisor", token);
    }

    [Fact]
    public void Parse_MentionsOnSeparateLines_BothMatch()
    {
        var body = "line one @supervisor\nline two @coder";
        var tokens = MentionTokenParser.Parse(body);
        Assert.Equal(new[] { "supervisor", "coder" }, tokens);
    }

    [Fact]
    public void Parse_MultipleAtSigns_OnlyBoundaryOnesMatch()
    {
        // '@@supervisor' — the first @ is at start (boundary via ^), but
        // the token attempt fails (@ is not a name start char). The second
        // @ is preceded by @ (punctuation, boundary), so 'supervisor'
        // matches. The first @ is NOT a separate mention.
        var tokens = MentionTokenParser.Parse("@@supervisor");
        var token = Assert.Single(tokens);
        Assert.Equal("supervisor", token);
    }

    [Fact]
    public void Parse_OnlyAtSign_YieldsNothing()
    {
        Assert.Empty(MentionTokenParser.Parse("@"));
        Assert.Empty(MentionTokenParser.Parse("@ @ @"));
    }

    [Fact]
    public void Parse_AtFollowedByPunctuationOnly_YieldsNothing()
    {
        // '@.' '@-' '@.' are not valid mentions — the name must start with
        // [A-Za-z0-9_].
        Assert.Empty(MentionTokenParser.Parse("@. hello"));
        Assert.Empty(MentionTokenParser.Parse("@- hello"));
    }
}
