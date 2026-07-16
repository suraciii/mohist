using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
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
    public async Task CreateIssue_WithStageModelsAndStageVariants_RoundTripsPerStageOverrides()
    {
        var projectId = await CreateProjectAsync("variant-stage-create");
        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "Stage variant create",
                stageModels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["plan"] = "openai/gpt-5.5",
                    ["build"] = "anthropic/claude-sonnet-4-20250514",
                },
                stageModelVariants = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["plan"] = "low",
                    ["build"] = "max",
                },
            });

        var detail = await _fixture.Client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{issue.GetProperty("number").GetInt32()}");

        var stageModels = detail.GetProperty("stageModels");
        var stageVariants = detail.GetProperty("stageModelVariants");

        Assert.Equal("openai/gpt-5.5", stageModels.GetProperty("plan").GetString());
        Assert.Equal("anthropic/claude-sonnet-4-20250514", stageModels.GetProperty("build").GetString());
        Assert.Equal("low", stageVariants.GetProperty("plan").GetString());
        Assert.Equal("max", stageVariants.GetProperty("build").GetString());
    }

    [Fact]
    public async Task ListIssues_IncludesModelAndVariantForIssueWithMetadata()
    {
        var projectId = await CreateProjectAsync("variant-list");
        await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "Variant listed",
                model = "openai/gpt-5.5",
                modelVariant = "low",
            });

        var list = await _fixture.Client.GetDataAsync<JsonElement[]>($"/api/projects/{projectId}/issues");

        var issue = Assert.Single(list, i => i.GetProperty("title").GetString() == "Variant listed");
        Assert.Equal("openai/gpt-5.5", issue.GetProperty("model").GetString());
        Assert.Equal("low", issue.GetProperty("modelVariant").GetString());
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
    public async Task PatchIssue_ClearingModel_ClearsVariantAtomically()
    {
        var projectId = await CreateProjectAsync("variant-clear");
        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "Cleared",
                model = "openai/gpt-5.5",
                modelVariant = "high",
            });

        var number = issue.GetProperty("number").GetInt32();

        await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{number}",
            new { model = (string?)null });

        var detail = await _fixture.Client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{number}");

        Assert.False(detail.TryGetProperty("model", out _));
        Assert.False(detail.TryGetProperty("modelVariant", out _));
    }

    [Fact]
    public async Task PatchIssue_ClearingStageModels_ClearsStageVariants()
    {
        var projectId = await CreateProjectAsync("variant-stage-clear");
        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "Stage clear",
                stageModels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["plan"] = "openai/gpt-5.5",
                },
                stageModelVariants = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["plan"] = "high",
                },
            });

        var number = issue.GetProperty("number").GetInt32();

        await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{number}",
            new { stageModels = (Dictionary<string, string>?)null });

        var detail = await _fixture.Client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{number}");

        Assert.False(detail.TryGetProperty("stageModels", out _));
        Assert.False(detail.TryGetProperty("stageModelVariants", out _));
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
    public async Task PatchIssue_WithStageModelsAndVariants_RoundTripsPerStageOverrides()
    {
        var projectId = await CreateProjectAsync("variant-stage-patch");
        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new { title = "Stage patch" });

        var number = issue.GetProperty("number").GetInt32();

        await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{number}",
            new
            {
                stageModels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["plan"] = "openai/gpt-5.5",
                    ["build"] = "anthropic/claude-sonnet-4-20250514",
                },
                stageModelVariants = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["plan"] = "low",
                    ["build"] = "max",
                },
            });

        var detail = await _fixture.Client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{number}");

        var stageModels = detail.GetProperty("stageModels");
        var stageVariants = detail.GetProperty("stageModelVariants");

        Assert.Equal("openai/gpt-5.5", stageModels.GetProperty("plan").GetString());
        Assert.Equal("anthropic/claude-sonnet-4-20250514", stageModels.GetProperty("build").GetString());
        Assert.Equal("low", stageVariants.GetProperty("plan").GetString());
        Assert.Equal("max", stageVariants.GetProperty("build").GetString());
    }

    [Fact]
    public async Task PatchIssue_SwitchingModel_DropsStaleVariant()
    {
        var projectId = await CreateProjectAsync("variant-switch");
        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "Switch",
                model = "anthropic/claude-opus-4-20250514",
                modelVariant = "high",
            });

        var number = issue.GetProperty("number").GetInt32();

        // PATCH a new model WITHOUT sending modelVariant — the stale variant
        // bound to the prior model must be cleared atomically because the
        // dependency invariant says a variant is only valid for its model.
        await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{number}",
            new { model = "openai/gpt-5.5" });

        var detail = await _fixture.Client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{number}");

        Assert.Equal("openai/gpt-5.5", detail.GetProperty("model").GetString());
        Assert.False(detail.TryGetProperty("modelVariant", out _));
    }

    [Fact]
    public async Task CreateIssue_WithEmptyModel_ClearsStaleVariantOnDetail()
    {
        // Sanity check: an issue created without any model has no modelVariant
        // (the dependency invariant applies even on create).
        var projectId = await CreateProjectAsync("variant-empty");

        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new { title = "No model" });

        var detail = await _fixture.Client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{issue.GetProperty("number").GetInt32()}");

        Assert.False(detail.TryGetProperty("model", out _));
        Assert.False(detail.TryGetProperty("modelVariant", out _));
    }

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
