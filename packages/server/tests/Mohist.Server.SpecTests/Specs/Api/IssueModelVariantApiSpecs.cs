using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class IssueModelVariantApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public IssueModelVariantApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateIssue_WithModelAndVariant_ReturnsBothInDetail()
    {
        var projectId = await CreateProjectAsync("variant-create");
        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "Variant create",
                model = "anthropic/claude-opus-4-20250514",
                modelVariant = "high",
            });

        var detail = await _fixture.Client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{issue.GetProperty("number").GetInt32()}");

        Assert.Equal("anthropic/claude-opus-4-20250514", detail.GetProperty("model").GetString());
        Assert.Equal("high", detail.GetProperty("modelVariant").GetString());
    }

    [Fact]
    public async Task ListIssues_ExcludesDetailOnlyModelFields()
    {
        var projectId = await CreateProjectAsync("variant-list");
        var created = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "Variant listed",
                model = "openai/gpt-5.5",
                modelVariant = "low",
            });

        var list = await _fixture.Client.GetDataAsync<JsonElement[]>($"/api/projects/{projectId}/issues");

        var issue = Assert.Single(list, i => i.GetProperty("title").GetString() == "Variant listed");
        Assert.False(issue.TryGetProperty("body", out _));
        Assert.False(issue.TryGetProperty("comments", out _));
        Assert.False(issue.TryGetProperty("attachments", out _));
        Assert.False(issue.TryGetProperty("feedback", out _));
        Assert.False(issue.TryGetProperty("model", out _));
        Assert.False(issue.TryGetProperty("modelVariant", out _));
        Assert.False(issue.TryGetProperty("agentConfig", out _));
        Assert.False(issue.TryGetProperty("stageModels", out _));
        Assert.False(issue.TryGetProperty("stageModelVariants", out _));

        var detail = await _fixture.Client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{created.GetProperty("number").GetInt32()}");
        Assert.Equal("openai/gpt-5.5", detail.GetProperty("model").GetString());
        Assert.Equal("low", detail.GetProperty("modelVariant").GetString());
    }

    [Fact]
    public async Task PatchIssue_WithModelAndVariant_UpdatesAndReturns()
    {
        var projectId = await CreateProjectAsync("variant-patch");
        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new { title = "Patch me" });

        var number = issue.GetProperty("number").GetInt32();

        await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{number}",
            new { model = "openai/gpt-5.5", modelVariant = "high" });

        var detail = await _fixture.Client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{number}");

        Assert.Equal("openai/gpt-5.5", detail.GetProperty("model").GetString());
        Assert.Equal("high", detail.GetProperty("modelVariant").GetString());
    }

    [Fact]
    public async Task CreateIssue_WithInvalidModelFormat_ReturnsBadRequest()
    {
        var projectId = await CreateProjectAsync("variant-invalid-create");

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "Bad model", model = "not-a-valid-model" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_model_metadata", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PatchIssue_WithInvalidModelFormat_ReturnsBadRequest()
    {
        var projectId = await CreateProjectAsync("variant-invalid-patch");
        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new { title = "Will be patched invalidly" });

        var number = issue.GetProperty("number").GetInt32();

        using var response = await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{number}",
            new { model = "no-slash-here" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_model_metadata", payload.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{\"model\":123}")]
    [InlineData("{\"modelVariant\":false}")]
    [InlineData("{\"stageModels\":[]}")]
    [InlineData("{\"stageModels\":{\"plan\":123}}")]
    [InlineData("{\"stageModelVariants\":[]}")]
    [InlineData("{\"stageModelVariants\":{\"plan\":true}}")]
    public async Task PatchIssue_WithWrongTypeModelMetadata_ReturnsBadRequest(string patchJson)
    {
        var projectId = await CreateProjectAsync("variant-type-invalid");
        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new { title = "Wrong type patch" });

        var number = issue.GetProperty("number").GetInt32();

        using var response = await _fixture.Client.PatchAsync(
            $"/api/projects/{projectId}/issues/{number}",
            new StringContent(patchJson, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_model_metadata", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateIssue_WithInvalidStageModelFormat_ReturnsBadRequest()
    {
        var projectId = await CreateProjectAsync("variant-stage-invalid");

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "Bad stage model",
                stageModels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["plan"] = "not-a-valid-model",
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_model_metadata", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OpencodeModels_RuntimeQueryReturnsSelectedCatalogAndDefaultsToOpenCode()
    {
        var projectId = await CreateProjectAsync("runtime-models");
        var runnerId = $"runtime-model-runner-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = Array.Empty<string>(),
            hostname = "runtime-model-host",
            runtimeCatalogs = new
            {
                opencode = new
                {
                    models = new[] { "openai/catalog-opencode" },
                    variants = new Dictionary<string, string[]> { ["openai/catalog-opencode"] = ["low"] },
                },
                pi = new
                {
                    models = new[] { "anthropic/catalog-pi" },
                    variants = new Dictionary<string, string[]> { ["anthropic/catalog-pi"] = ["balanced"] },
                    reasoningEfforts = new Dictionary<string, string[]> { ["anthropic/catalog-pi"] = ["high"] },
                },
            },
        });

        try
        {
            var pi = await _fixture.Client.GetDataAsync<CatalogDto>($"/api/projects/{projectId}/opencode/models?runtime=pi");
            var opencode = await _fixture.Client.GetDataAsync<CatalogDto>($"/api/projects/{projectId}/opencode/models?runtime=opencode");
            var defaultCatalog = await _fixture.Client.GetDataAsync<CatalogDto>($"/api/projects/{projectId}/opencode/models");

            Assert.Contains("anthropic/catalog-pi", pi.Models);
            Assert.DoesNotContain("openai/catalog-opencode", pi.Models);
            Assert.Equal(["balanced"], pi.ModelVariants["anthropic/catalog-pi"]);
            Assert.Equal(["high"], pi.ReasoningEfforts["anthropic/catalog-pi"]);
            Assert.Contains("openai/catalog-opencode", opencode.Models);
            Assert.Equal(["low"], opencode.ModelVariants["openai/catalog-opencode"]);
            Assert.Contains("openai/catalog-opencode", defaultCatalog.Models);
            Assert.DoesNotContain("anthropic/catalog-pi", defaultCatalog.Models);
            Assert.Equal(opencode.ModelVariants, defaultCatalog.ModelVariants);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private sealed record CatalogDto(
        string[] Models,
        Dictionary<string, string[]> ModelVariants,
        Dictionary<string, string[]> ReasoningEfforts);

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var response = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<JsonElement>("/api/projects", $"{prefix}-{Guid.NewGuid():N}");
        var projectId = response.GetProperty("id").GetString()!;
        await _fixture.Client.PostOkAsync($"/api/projects/{projectId}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return projectId;
    }
}
