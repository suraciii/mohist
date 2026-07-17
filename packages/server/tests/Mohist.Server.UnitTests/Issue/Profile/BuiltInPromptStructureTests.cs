using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class BuiltInPromptStructureTests
{
    [Fact]
    public void BuiltInPrompts_AllParseWithRequiredFrontmatterFields()
    {
        var templates = new FilePromptLoader().LoadAllTemplates();

        Assert.NotEmpty(templates);
        foreach (var (key, template) in templates)
        {
            Assert.False(string.IsNullOrWhiteSpace(template.DisplayName), $"{key}: DisplayName is required");
            Assert.False(string.IsNullOrWhiteSpace(template.Description), $"{key}: Description is required");
            Assert.False(string.IsNullOrWhiteSpace(template.Body), $"{key}: Body is required");
        }
    }
}
