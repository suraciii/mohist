using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue")]
public class IssuePatchRawPresenceMergeSpecs
{
    private readonly HttpClient _client;

    public IssuePatchRawPresenceMergeSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task PatchIssue_OnUnknownIssue_Returns404()
    {
        var project = await CreateProjectAsync("not-found");

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/999999",
            new StringContent("{\"title\":\"new\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchIssue_WithInvalidJson_Returns400()
    {
        var project = await CreateProjectAsync("bad-json");
        var issue = await CreateIssueAsync(project.Id, title: "Original");

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new StringContent("{not-json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchIssue_StageModelVariants_PersistsViaWorkflowProfilePath()
    {
        var project = await CreateProjectAsync("stage-variants");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Variant issue", projectId = project.Id });

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new StringContent(
                "{\"stageModels\":{\"plan\":\"openai/gpt-5.5\"},\"stageModelVariants\":{\"plan\":\"max\"}}",
                Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();

        // The variant is stored on the workflow profile's variables bundle
        // (stages.plan.vars.agent.variant) and is projected alongside the
        // stage model.
        var detail = await ReadDataAsync<IssueDto>(response);
        Assert.NotNull(detail.StageModels);
        Assert.Equal("openai/gpt-5.5", detail.StageModels!["plan"]);
        Assert.NotNull(detail.StageModelVariants);
        Assert.Equal("max", detail.StageModelVariants!["plan"]);
    }

    [Fact]
    public async Task CreateIssue_WithStageModels_PersistsViaWorkflowProfilePath()
    {
        var project = await CreateProjectAsync("create-stage-models");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Created with stages",
                projectId = project.Id,
                stageModels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["plan"] = "openai/gpt-5.5",
                },
                model = "openai/gpt-5.5",
            });
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<IssueDto>(response);
        Assert.Equal("openai/gpt-5.5", detail.Model);
        Assert.NotNull(detail.StageModels);
        Assert.Equal("openai/gpt-5.5", detail.StageModels!["plan"]);
    }

    private async Task<ProjectDto> CreateProjectAsync(string prefix)
    {
        var project = await _client.PostDataAsync<ProjectDto>(
            "/api/projects",
            new
            {
                name = $"p-{prefix}-{Guid.NewGuid():N}",
                repository = new
                {
                    name = "main",
                    gitUrl = $"file://{Guid.NewGuid():N}",
                    baseBranch = "main",
                },
            });
        return project;
    }

    private async Task<IssueDto> CreateIssueAsync(
        string projectId,
        string title,
        Dictionary<string, string>? labels = null)
    {
        return await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title,
                projectId,
                labels,
            });
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (!envelope.GetProperty("success").GetBoolean())
        {
            var error = envelope.TryGetProperty("error", out var err) ? err.GetString() : "<no error>";
            throw new InvalidOperationException($"API request failed: {error}");
        }
        var data = envelope.GetProperty("data");
        return JsonSerializer.Deserialize<T>(data.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(
        int Number,
        string Id,
        string Title,
        string? Body,
        Dictionary<string, string> Labels,
        string Priority,
        bool IsDraft,
        string[] AttachmentIds,
        string? Model,
        Dictionary<string, string>? StageModels,
        Dictionary<string, string>? StageModelVariants);
}
