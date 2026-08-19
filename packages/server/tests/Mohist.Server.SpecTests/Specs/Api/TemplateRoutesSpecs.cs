using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

public class TemplateRoutesSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public TemplateRoutesSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListSystemTemplates_ReturnsAllBuiltInTemplatesSortedByKey()
    {
        using var response = await _fixture.Client.GetAsync("/api/templates/system");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("success").GetBoolean());

        var data = payload.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);

        var keys = data.EnumerateArray()
            .Select(item => item.GetProperty("key").GetString()!)
            .ToArray();

        Assert.Equal(13, keys.Length);
        var expectedKeys = new[]
        {
            "apply-feedback",
            "auto-fix",
            "build",
            "design",
            "fix-ci",
            "fix-plan-review",
            "fix-pr-checks",
            "proposal",
            "resolve-rebase-conflicts",
            "review",
            "self-review",
            "specs",
            "tasks",
        };
        Assert.Equal(expectedKeys, keys);
    }

    [Fact]
    public async Task ListSystemTemplates_ProposalExposesFrontmatterAndBody()
    {
        using var response = await _fixture.Client.GetAsync("/api/templates/system");

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");

        var proposal = data.EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "proposal");

        Assert.Equal("Generate Proposal", proposal.GetProperty("displayName").GetString());
        Assert.Equal(
            "Creates the OpenSpec proposal.md for an issue",
            proposal.GetProperty("description").GetString());

        var tags = proposal.GetProperty("tags").EnumerateArray()
            .Select(tag => tag.GetString())
            .ToArray();
        Assert.Equal(new[] { "plan", "openspec" }, tags);
        Assert.Equal("plan", proposal.GetProperty("stage").GetString());

        var body = proposal.GetProperty("body").GetString();
        Assert.NotNull(body);
        Assert.DoesNotContain("---", body);
        Assert.Contains("artifact", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractVariables_WithValidBody_ReturnsSortedUniquePaths()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/templates/extract-variables",
             new { body = "Use openspec/changes/issue-${{ issue.number }} and ${{ issue.number }} and openspec/changes/issue-${{ issue.number }}" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("success").GetBoolean());

        var variables = payload.GetProperty("data").GetProperty("variables")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

         Assert.Equal(new[] { "issue.number" }, variables);
    }

    [Fact]
    public async Task ExtractVariables_WithUnresolvableRefs_StillReturnsSortedPaths()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/templates/extract-variables",
            new { body = "${{ does.not.exist }} nested with ${{ another.missing }}" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var variables = payload.GetProperty("data").GetProperty("variables")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Equal(new[] { "another.missing", "does.not.exist" }, variables);
    }

    [Fact]
    public async Task ExtractVariables_IgnoresEscapedReferences()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/templates/extract-variables",
            new { body = @"Use \${{ vars.literal }} and ${{ vars.actual }}" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var variables = payload.GetProperty("data").GetProperty("variables")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Equal(new[] { "vars.actual" }, variables);
    }

    [Fact]
    public async Task ExtractVariables_WithContextReturnsRendererErrors()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/templates/extract-variables",
            new
            {
                body = "value=${{ vars.missing }} and ${{ vars.object }}",
                variables = new { vars = new { @object = new { value = 1 } } },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = payload.GetProperty("data").GetProperty("errors");
        Assert.Contains(errors.EnumerateArray(), error =>
            error.GetProperty("code").GetString() == "missing_reference" &&
            error.GetProperty("path").GetString() == "vars.missing");
        Assert.Contains(errors.EnumerateArray(), error =>
            error.GetProperty("code").GetString() == "invalid_type" &&
            error.GetProperty("path").GetString() == "vars.object");
    }

    [Fact]
    public async Task ExtractVariables_WithEmptyBody_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/templates/extract-variables",
            new { body = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("bad_request", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExtractVariables_WithWhitespaceBody_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/templates/extract-variables",
            new { body = "   \t  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("bad_request", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExtractVariables_WithMissingBody_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/templates/extract-variables",
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("bad_request", payload.GetProperty("code").GetString());
    }
}
