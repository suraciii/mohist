using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.IntegrationSpecs.Support;
using Xunit;

namespace Mohist.Server.IntegrationSpecs.Specs.Issue.Profile;

[Collection("IntegrationIssueConfiguration")]
public class MohistLocalWorkflowProfileStartWorkSpecs
{
    private readonly HttpClient _client;

    public MohistLocalWorkflowProfileStartWorkSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task StartWork_WithUnknownPromptReference_Returns400MissingPromptsWithMissingKeysDetails()
    {
        var project = await _client.PostDataAsync<StartProjectDto>("/api/projects", new { name = $"missing-prompts-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<StartIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workflow references unknown prompt", projectId = project.Id, isDraft = false });

        var customYaml = """
            id: missing-prompt-workflow
            stages:
              - stage: plan
                tasks:
                  - id: missing-prompt-task
                    title: Missing prompt task
                    uses: mohist/acp-agent
                    with:
                      prompt: ${{ prompts.does-not-exist }}
                checks: []
            """;
        await _client.PutAsJsonOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template", new { yaml = customYaml });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("missing_prompts", payload.GetProperty("code").GetString());
        var missingKeys = payload.GetProperty("details").GetProperty("missingKeys").EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Contains("does-not-exist", missingKeys);
    }

    [Fact]
    public async Task StartWork_WithMultipleUnknownPromptReferences_ReturnsAllMissingKeysInDetails()
    {
        var project = await _client.PostDataAsync<StartProjectDto>("/api/projects", new { name = $"multi-missing-prompts-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<StartIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workflow references multiple unknown prompts", projectId = project.Id, isDraft = false });

        var customYaml = """
            id: multi-missing-prompt-workflow
            stages:
              - stage: plan
                tasks:
                  - id: multi-missing-prompt-task
                    title: Multi missing prompt task
                    uses: mohist/acp-agent
                    with:
                      prompt: ${{ prompts.ghost-one }} and ${{ prompts.ghost-two }}
                checks: []
            """;
        await _client.PutAsJsonOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template", new { yaml = customYaml });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("missing_prompts", payload.GetProperty("code").GetString());
        var missingKeys = payload.GetProperty("details").GetProperty("missingKeys").EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Contains("ghost-one", missingKeys);
        Assert.Contains("ghost-two", missingKeys);
    }

    [Fact]
    public async Task StartWork_WithKnownSystemPromptKey_DoesNotEmitMissingPromptsError()
    {
        var project = await _client.PostDataAsync<StartProjectDto>("/api/projects", new { name = $"known-prompts-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<StartIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workflow references known prompt", projectId = project.Id, isDraft = false });

        var customYaml = """
            id: known-prompt-workflow
            stages:
              - stage: plan
                tasks:
                  - id: known-prompt-task
                    title: Known prompt task
                    uses: mohist/acp-agent
                    with:
                      prompt: ${{ prompts.proposal }}
                checks: []
            """;
        await _client.PutAsJsonOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template", new { yaml = customYaml });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record StartProjectDto(string Id);
    private sealed record StartIssueDto(int Number, string Id);
}
