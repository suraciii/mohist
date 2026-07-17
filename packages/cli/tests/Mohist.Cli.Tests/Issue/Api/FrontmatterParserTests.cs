using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Issue.Api;

public class FrontmatterParserTests
{
    [Fact]
    public void Parse_CompleteFrontmatter_ExtractsAllFieldsAndStripsBlockFromBody()
    {
        var text = "---\n"
            + "recommended_workflow: feature-flow\n"
            + "recommended_workflow_reason: Matches UI and feature scope\n"
            + "risk: high\n"
            + "---\n"
            + "## Background\n"
            + "Real body content.\n";

        var result = FrontmatterParser.Parse(text);

        var ok = Assert.IsType<FrontmatterParser.Result.Parsed>(result);
        Assert.Equal("feature-flow", ok.RecommendedWorkflow);
        Assert.Equal("Matches UI and feature scope", ok.RecommendedWorkflowReason);
        Assert.Equal("high", ok.Risk);
        Assert.Equal("## Background\nReal body content.\n", ok.Body);
    }

    [Fact]
    public void Parse_BlockScalarReason_PreservesMultilineValue()
    {
        var text = "---\n"
            + "recommended_workflow: feature-flow\n"
            + "recommended_workflow_reason: |\n"
            + "  Line one of the reason.\n"
            + "  Line two of the reason.\n"
            + "risk: medium\n"
            + "---\n"
            + "Body only.\n";

        var result = FrontmatterParser.Parse(text);

        var ok = Assert.IsType<FrontmatterParser.Result.Parsed>(result);
        Assert.Equal("feature-flow", ok.RecommendedWorkflow);
        Assert.Equal("Line one of the reason.\nLine two of the reason.", ok.RecommendedWorkflowReason);
        Assert.Equal("medium", ok.Risk);
        Assert.Equal("Body only.\n", ok.Body);
    }

    [Fact]
    public void Parse_PartialFrontmatter_AppliesPresentFieldsAndLeavesOthersNull()
    {
        var text = "---\n"
            + "recommended_workflow: feature-flow\n"
            + "---\n"
            + "Body.\n";

        var result = FrontmatterParser.Parse(text);

        var ok = Assert.IsType<FrontmatterParser.Result.Parsed>(result);
        Assert.Equal("feature-flow", ok.RecommendedWorkflow);
        Assert.Null(ok.RecommendedWorkflowReason);
        Assert.Null(ok.Risk);
        Assert.Equal("Body.\n", ok.Body);
    }

    [Fact]
    public void Parse_UnrecognizedFields_AreSilentlyIgnored()
    {
        var text = "---\n"
            + "title: ignored\n"
            + "recommended_workflow: feature-flow\n"
            + "custom_field: whatever\n"
            + "risk: low\n"
            + "---\n"
            + "Body.\n";

        var result = FrontmatterParser.Parse(text);

        var ok = Assert.IsType<FrontmatterParser.Result.Parsed>(result);
        Assert.Equal("feature-flow", ok.RecommendedWorkflow);
        Assert.Equal("low", ok.Risk);
        Assert.Null(ok.RecommendedWorkflowReason);
        Assert.Equal("Body.\n", ok.Body);
    }

    [Fact]
    public void Parse_QuotedValues_StripsSurroundingQuotes()
    {
        var text = "---\n"
            + "recommended_workflow: \"feature-flow\"\n"
            + "risk: 'high'\n"
            + "---\n"
            + "Body.\n";

        var result = FrontmatterParser.Parse(text);

        var ok = Assert.IsType<FrontmatterParser.Result.Parsed>(result);
        Assert.Equal("feature-flow", ok.RecommendedWorkflow);
        Assert.Equal("high", ok.Risk);
    }

    [Fact]
    public void Parse_NoLeadingDelimiter_ReturnsNotFoundWithFullBody()
    {
        var text = "## Just a body\nno frontmatter at all\n";

        var result = FrontmatterParser.Parse(text);

        var missing = Assert.IsType<FrontmatterParser.Result.NotFound>(result);
        Assert.Equal(text, missing.Body);
    }

    [Fact]
    public void Parse_EmptyOrNullText_ReturnsNotFound()
    {
        var empty = FrontmatterParser.Parse(string.Empty);
        Assert.IsType<FrontmatterParser.Result.NotFound>(empty);

        var @null = FrontmatterParser.Parse(null);
        var missing = Assert.IsType<FrontmatterParser.Result.NotFound>(@null);
        Assert.Equal(string.Empty, missing.Body);
    }

    [Fact]
    public void Parse_MissingClosingDelimiter_ReturnsMalformedWithFullBody()
    {
        var text = "---\n"
            + "recommended_workflow: feature-flow\n"
            + "no closing delimiter here\n";

        var result = FrontmatterParser.Parse(text);

        var malformed = Assert.IsType<FrontmatterParser.Result.Malformed>(result);
        Assert.Equal(text, malformed.Body);
    }

    [Fact]
    public void Parse_LineWithoutColon_ReturnsMalformed()
    {
        var text = "---\n"
            + "recommended_workflow: feature-flow\n"
            + "this line has no colon\n"
            + "---\n"
            + "Body.\n";

        var result = FrontmatterParser.Parse(text);

        Assert.IsType<FrontmatterParser.Result.Malformed>(result);
    }

    [Fact]
    public void Parse_HorizontalRuleInBodyNotTreatedAsFrontmatter()
    {
        var text = "# Title\n\n---\n\na horizontal rule, not frontmatter\n";

        var result = FrontmatterParser.Parse(text);

        Assert.IsType<FrontmatterParser.Result.NotFound>(result);
    }

    [Fact]
    public void Parse_BodyIsEmptyAfterClosingDelimiter()
    {
        var text = "---\n"
            + "recommended_workflow: feature-flow\n"
            + "risk: high\n"
            + "---\n";

        var result = FrontmatterParser.Parse(text);

        var ok = Assert.IsType<FrontmatterParser.Result.Parsed>(result);
        Assert.Equal(string.Empty, ok.Body);
        Assert.Equal("feature-flow", ok.RecommendedWorkflow);
        Assert.Equal("high", ok.Risk);
    }
}
