using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.L1Tests.Support;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Api;

[Trait("level", "L1")]
public class TemplateRoutesSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;

    public TemplateRoutesSpecs(DefaultMohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ListSystemTemplates_ReturnsSimplifiedBuiltInsSortedByKey()
    {
        using var response = await _fixture.Client.GetAsync("/api/templates/system");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var keys = payload.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("key").GetString()!).ToArray();
        Assert.Equal(new[] { "apply-feedback", "build-task", "fix-ci", "fix-pr-checks", "plan", "resolve-rebase-conflicts", "review" }, keys);
    }

    [Fact]
    public async Task ListSystemTemplates_PlanExposesFrontmatterAndBody()
    {
        using var response = await _fixture.Client.GetAsync("/api/templates/system");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var plan = payload.GetProperty("data").EnumerateArray().Single(item => item.GetProperty("key").GetString() == "plan");
        Assert.Equal("Plan Change", plan.GetProperty("displayName").GetString());
        Assert.Equal("plan", plan.GetProperty("stage").GetString());
        Assert.Contains("plan artifact", plan.GetProperty("body").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractVariables_WithValidBody_ReturnsSortedUniquePaths()
    {
        var response = await _fixture.Client.PostAsJsonAsync("/api/templates/extract-variables", new { body = "Use ${{ issue.number }} and ${{ issue.number }}" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "issue.number" }, payload.GetProperty("data").GetProperty("variables").EnumerateArray().Select(item => item.GetString()));
    }
}
