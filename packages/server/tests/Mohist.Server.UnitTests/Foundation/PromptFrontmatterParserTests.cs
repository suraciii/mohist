using Mohist.Server.Workflow.Domain.Prompts;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Foundation;

public class PromptFrontmatterParserTests
{
    [Fact]
    public void Parse_WellFormedFrontmatter_ExposesAllFourFieldsAndStripsBlock()
    {
        const string fileText = "---\n"
            + "name: \"Generate Proposal\"\n"
            + "description: \"Creates the plan proposal.md for an issue\"\n"
            + "tags: [plan, artifacts]\n"
            + "stage: plan\n"
            + "---\n"
            + "BODY LINE 1\n"
            + "BODY LINE 2\n";

        var (frontmatter, body) = PromptFrontmatterParser.Parse(fileText, "proposal");

        Assert.Equal("Generate Proposal", frontmatter.Name);
        Assert.Equal("Creates the plan proposal.md for an issue", frontmatter.Description);
        Assert.Equal(new[] { "plan", "artifacts" }, frontmatter.Tags);
        Assert.Equal("plan", frontmatter.Stage);
        Assert.Equal("BODY LINE 1\nBODY LINE 2\n", body);
        Assert.DoesNotContain("---", body);
    }

    [Fact]
    public void Parse_MissingFrontmatter_ReturnsDefaultsAndFullTextAsBody()
    {
        const string fileText = "no frontmatter here\njust a body\n";

        var (frontmatter, body) = PromptFrontmatterParser.Parse(fileText, "proposal");

        Assert.Null(frontmatter.Name);
        Assert.Equal(string.Empty, frontmatter.Description);
        Assert.Empty(frontmatter.Tags);
        Assert.Null(frontmatter.Stage);
        Assert.Equal(fileText, body);
    }

    [Fact]
    public void Parse_PartialFrontmatterWithOnlyName_DefaultsOtherFieldsAndKeepsBodyValid()
    {
        const string fileText = "---\n"
            + "name: \"Only Name\"\n"
            + "---\n"
            + "remaining body content\n";

        var (frontmatter, body) = PromptFrontmatterParser.Parse(fileText, "partial");

        Assert.Equal("Only Name", frontmatter.Name);
        Assert.Equal(string.Empty, frontmatter.Description);
        Assert.Empty(frontmatter.Tags);
        Assert.Null(frontmatter.Stage);
        Assert.Equal("remaining body content\n", body);
    }

    [Fact]
    public void Parse_MalformedYaml_ThrowsPromptFrontmatterParseExceptionWithKeyInMessage()
    {
        const string fileText = "---\n"
            + "name: \"unclosed string\n"
            + "description: oops\n"
            + "---\n"
            + "body\n";

        var ex = Assert.Throws<PromptFrontmatterParseException>(
            () => PromptFrontmatterParser.Parse(fileText, "broken-key"));

        Assert.Contains("broken-key", ex.Message);
    }
}
