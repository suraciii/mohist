using Mohist.Server.Workflow.Domain.Prompts;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs;

public class PromptFrontmatterParserSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void Parse_WellFormedFrontmatter_ExposesAllFourFieldsAndStripsBlock()
    {
        const string fileText = "---\n"
            + "name: \"Generate Proposal\"\n"
            + "description: \"Creates the OpenSpec proposal.md for an issue\"\n"
            + "tags: [plan, openspec]\n"
            + "stage: plan\n"
            + "---\n"
            + "BODY LINE 1\n"
            + "BODY LINE 2\n";

        var (frontmatter, body) = PromptFrontmatterParser.Parse(fileText, "proposal");

        Assert.Equal("Generate Proposal", frontmatter.Name);
        Assert.Equal("Creates the OpenSpec proposal.md for an issue", frontmatter.Description);
        Assert.Equal(new[] { "plan", "openspec" }, frontmatter.Tags);
        Assert.Equal("plan", frontmatter.Stage);
        Assert.Equal("BODY LINE 1\nBODY LINE 2\n", body);
        Assert.DoesNotContain("---", body);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
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
