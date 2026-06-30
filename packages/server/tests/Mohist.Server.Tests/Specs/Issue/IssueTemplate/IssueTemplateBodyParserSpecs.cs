using Mohist.Server.Issue.Services.IssueTemplates;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.IssueTemplate;

public class IssueTemplateBodyParserSpecs
{
    private static string ReadTemplateFile(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Issue/Services/IssueTemplates/templates");
        var path = Path.Combine(dir, $"{name}.md");
        return File.ReadAllText(path);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void FeatureTemplate_HasExpectedSections()
    {
        var content = ReadTemplateFile("feature");
        var (_, body) = ParseFrontmatter(content);
        var sections = IssueTemplateBodyParser.Parse(body);

        Assert.Equal(5, sections.Count);
        Assert.Equal("User Voice", sections[0].Title);
        Assert.Equal("Product Shape", sections[1].Title);
        Assert.Equal("Domain Model", sections[2].Title);
        Assert.Equal("Acceptance Criteria", sections[3].Title);
        Assert.Equal("Non-Goals", sections[4].Title);

        foreach (var section in sections)
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Guidance),
                $"Section '{section.Title}' has empty guidance");
            Assert.False(string.IsNullOrWhiteSpace(section.Placeholder),
                $"Section '{section.Title}' has empty placeholder");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void BugTemplate_HasExpectedSections()
    {
        var content = ReadTemplateFile("bug");
        var (_, body) = ParseFrontmatter(content);
        var sections = IssueTemplateBodyParser.Parse(body);

        Assert.Equal(5, sections.Count);
        Assert.Equal("Symptom & Evidence", sections[0].Title);
        Assert.Equal("Domain Context", sections[1].Title);
        Assert.Equal("Fix Shape", sections[2].Title);
        Assert.Equal("Acceptance Criteria", sections[3].Title);
        Assert.Equal("Non-Goals", sections[4].Title);

        foreach (var section in sections)
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Guidance),
                $"Section '{section.Title}' has empty guidance");
            Assert.False(string.IsNullOrWhiteSpace(section.Placeholder),
                $"Section '{section.Title}' has empty placeholder");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void RefactorTemplate_HasExpectedSections()
    {
        var content = ReadTemplateFile("refactor");
        var (_, body) = ParseFrontmatter(content);
        var sections = IssueTemplateBodyParser.Parse(body);

        Assert.Equal(5, sections.Count);
        Assert.Equal("Motivation", sections[0].Title);
        Assert.Equal("Change Scope", sections[1].Title);
        Assert.Equal("Behavior Contract", sections[2].Title);
        Assert.Equal("Done When", sections[3].Title);
        Assert.Equal("Non-Goals", sections[4].Title);

        foreach (var section in sections)
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Guidance),
                $"Section '{section.Title}' has empty guidance");
            Assert.False(string.IsNullOrWhiteSpace(section.Placeholder),
                $"Section '{section.Title}' has empty placeholder");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void FeatureTemplate_DomainModelSection_GuidanceIsOptional()
    {
        var content = ReadTemplateFile("feature");
        var (_, body) = ParseFrontmatter(content);
        var sections = IssueTemplateBodyParser.Parse(body);

        var domainModel = sections[2];
        Assert.Equal("Domain Model", domainModel.Title);
        Assert.Contains("Optional", domainModel.Guidance);
        Assert.Contains("Optional", domainModel.Placeholder);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void BugTemplate_SymptomSection_HasDetailedGuidance()
    {
        var content = ReadTemplateFile("bug");
        var (_, body) = ParseFrontmatter(content);
        var sections = IssueTemplateBodyParser.Parse(body);

        var symptom = sections[0];
        Assert.Equal("Symptom & Evidence", symptom.Title);
        Assert.Contains("repro steps", symptom.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Name, string body) ParseFrontmatter(string content)
    {
        var lines = content.Replace("\r", string.Empty).Split('\n');
        if (lines.Length < 2 || lines[0] != "---") return (null, content);

        var closingIndex = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i] == "---")
            {
                closingIndex = i;
                break;
            }
        }

        if (closingIndex < 0) return (null, content);

        var body = string.Join("\n", lines, closingIndex + 1, lines.Length - closingIndex - 1);
        return (null, body);
    }
}
