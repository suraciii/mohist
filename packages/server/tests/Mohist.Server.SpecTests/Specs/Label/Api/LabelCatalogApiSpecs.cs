using System.Net;
using System.Net.Http.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Label.Api;

[Collection("IntegrationApi")]
public class LabelCatalogApiSpecs
{
    private readonly HttpClient _client;

    public LabelCatalogApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task GetCatalog_WithUserDefinitions_ReturnsThem()
    {
        var project = await CreateProjectAsync("merged-catalog");

        await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog",
            new { key = "module", description = "Classifies the subsystem", supportedValues = new[] { "auth", "ui" } });

        var definitions = await _client.GetDataAsync<LabelDefinitionDto[]>(
            $"/api/projects/{project.Id}/labels/catalog");

        var module = Assert.Single(definitions, d => d.Key == "module");
        Assert.Equal("Classifies the subsystem", module.Description);
        Assert.NotNull(module.SupportedValues);
        Assert.Contains("auth", module.SupportedValues);
        Assert.Contains("ui", module.SupportedValues);
    }

    [Fact]
    public async Task PostCatalog_CreatesDefinition_Returns201()
    {
        var project = await CreateProjectAsync("create-catalog");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog",
            new { key = "module", description = "Classifies the subsystem", supportedValues = new[] { "auth", "ui" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<LabelDefinitionDto>>();
        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.NotNull(envelope.Data);
        Assert.Equal("module", envelope.Data.Key);
        Assert.Equal("Classifies the subsystem", envelope.Data.Description);
        Assert.NotNull(envelope.Data.SupportedValues);
        Assert.Equal(2, envelope.Data.SupportedValues.Count);
    }

    [Fact]
    public async Task PostCatalog_DuplicateKey_Returns409()
    {
        var project = await CreateProjectAsync("dup-catalog");

        await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog",
            new { key = "module", description = "First" });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog",
            new { key = "module", description = "Second" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("already exists", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostCatalog_InvalidKey_Returns400()
    {
        var project = await CreateProjectAsync("invalid-key");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog",
            new { key = "Module", description = "Classifies" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PatchCatalog_UpdatesDescription_Returns200()
    {
        var project = await CreateProjectAsync("patch-catalog");

        await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog",
            new { key = "module", description = "Original description" });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog/module",
            new { description = "Updated description" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<LabelDefinitionDto>>();
        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.Equal("module", envelope.Data!.Key);
        Assert.Equal("Updated description", envelope.Data.Description);
    }

    [Fact]
    public async Task PatchCatalog_UpdatesSupportedValuesOnly_PreservesDescription()
    {
        var project = await CreateProjectAsync("patch-values-only");

        await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog",
            new { key = "module", description = "Original description", supportedValues = new[] { "auth" } });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog/module",
            new { supportedValues = new[] { "ui", "data" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<LabelDefinitionDto>>();
        Assert.NotNull(envelope?.Data);
        Assert.Equal("Original description", envelope.Data.Description);
        Assert.NotNull(envelope.Data.SupportedValues);
        Assert.Equal(new[] { "ui", "data" }, envelope.Data.SupportedValues);
    }

    [Fact]
    public async Task PatchCatalog_ClearsSupportedValuesOnly_PreservesDescription()
    {
        var project = await CreateProjectAsync("patch-clear-values");

        await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog",
            new { key = "module", description = "Original description", supportedValues = new[] { "auth" } });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog/module",
            new { supportedValues = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<LabelDefinitionDto>>();
        Assert.NotNull(envelope?.Data);
        Assert.Equal("Original description", envelope.Data.Description);
        Assert.Null(envelope.Data.SupportedValues);
    }

    [Fact]
    public async Task PatchCatalog_EmptyDescription_Returns400AndDoesNotMutate()
    {
        var project = await CreateProjectAsync("patch-empty-desc");

        await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog",
            new { key = "module", description = "Original description", supportedValues = new[] { "auth" } });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog/module",
            new { description = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("non-empty", body, StringComparison.OrdinalIgnoreCase);

        var definitions = await _client.GetDataAsync<LabelDefinitionDto[]>(
            $"/api/projects/{project.Id}/labels/catalog");
        var module = Assert.Single(definitions, d => d.Key == "module");
        Assert.Equal("Original description", module.Description);
        Assert.NotNull(module.SupportedValues);
        Assert.Equal(new[] { "auth" }, module.SupportedValues);
    }

    [Fact]
    public async Task PatchCatalog_EmptySupportedValue_Returns400AndDoesNotMutate()
    {
        var project = await CreateProjectAsync("patch-empty-sv");

        await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog",
            new { key = "module", description = "Original description", supportedValues = new[] { "auth" } });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog/module",
            new { supportedValues = new[] { "ui", "", "data" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("non-empty", body, StringComparison.OrdinalIgnoreCase);

        var definitions = await _client.GetDataAsync<LabelDefinitionDto[]>(
            $"/api/projects/{project.Id}/labels/catalog");
        var module = Assert.Single(definitions, d => d.Key == "module");
        Assert.Equal("Original description", module.Description);
        Assert.NotNull(module.SupportedValues);
        Assert.Equal(new[] { "auth" }, module.SupportedValues);
    }

    [Fact]
    public async Task PatchCatalog_MissingKey_Returns404()
    {
        var project = await CreateProjectAsync("patch-missing");

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog/nonexistent",
            new { description = "Wont work" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCatalog_RemovesDefinition_Returns204()
    {
        var project = await CreateProjectAsync("del-catalog");

        await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/labels/catalog",
            new { key = "module", description = "Classifies the subsystem" });

        using var response = await _client.DeleteAsync(
            $"/api/projects/{project.Id}/labels/catalog/module");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var definitions = await _client.GetDataAsync<LabelDefinitionDto[]>(
            $"/api/projects/{project.Id}/labels/catalog");
        Assert.DoesNotContain(definitions, d => d.Key == "module");
    }

    [Fact]
    public async Task Catalog_IsProjectScoped()
    {
        var projectA = await CreateProjectAsync("scope-a");
        var projectB = await CreateProjectAsync("scope-b");

        await _client.PostAsJsonAsync(
            $"/api/projects/{projectA.Id}/labels/catalog",
            new { key = "module", description = "Project A module" });

        var catalogA = await _client.GetDataAsync<LabelDefinitionDto[]>(
            $"/api/projects/{projectA.Id}/labels/catalog");
        var catalogB = await _client.GetDataAsync<LabelDefinitionDto[]>(
            $"/api/projects/{projectB.Id}/labels/catalog");

        Assert.Contains(catalogA, d => d.Key == "module");
        Assert.DoesNotContain(catalogB, d => d.Key == "module");
    }

    [Fact]
    public async Task DistinctKeysEndpoint_IsUnchanged()
    {
        var project = await CreateProjectAsync("distinct-check");

        await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Labeled issue",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stream"] = "frontend",
                    ["module"] = "auth",
                },
            });

        var labels = await _client.GetDataAsync<string[]>($"/api/projects/{project.Id}/labels");

        Assert.Equal(new[] { "module", "stream" }, labels);
    }

    private async Task<ProjectDto> CreateProjectAsync(string prefix)
    {
        return await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"cat-{prefix}-{Guid.NewGuid():N}",
            gitUrl: $"file://{Guid.NewGuid():N}");
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number);

    private sealed record LabelDefinitionDto(
        string Key,
        string Description,
        IReadOnlyList<string>? SupportedValues = null);

    private sealed record ApiEnvelope<T>(bool Success, T? Data, string? Error = null, string? Code = null, object? Details = null);
}
