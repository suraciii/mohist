using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Api;

/// <summary>
/// HTTP API regression coverage for issue-workflow-profile consistency
/// (issue #257 T-002). Verifies that:
///   - POST /api/issues with workflowProfileId persists the selection
///   - POST without the field inherits the default on reads
///   - POST with an unknown id is rejected with 400
///   - PATCH honors three-state semantics on workflowProfileId
///   - PATCH on a started issue is rejected with 409 and leaves the
///     selection unchanged
///   - PUT .../workflow-profile/variables still succeeds on a started
///     issue and the configured variables are preserved across a
///     profile-selection update
/// </summary>
[Collection("MohistIntegration")]
public class IssueWorkflowProfileApiConsistencySpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private string? _startedProjectId;
    private int _startedIssueNumber;

    public IssueWorkflowProfileApiConsistencySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_startedProjectId) && _startedIssueNumber > 0)
        {
            using var _ = await _client.PostAsync($"/api/projects/{_startedProjectId}/issues/{_startedIssueNumber}/stop", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithPrWorkflowProfile_PersistsAndReadModelAgrees()
    {
        var project = await CreateProjectAsync("wfp-create-pr");

        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "PR profile issue",
                projectId = project.Id,
                workflowProfileId = "mohist/pr",
            });

        Assert.Equal("mohist/pr", issue.WorkflowProfileId);

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/pr", detail.WorkflowProfileId);

        var listed = await _client.GetDataAsync<IssueDto[]>($"/api/projects/{project.Id}/issues?all=true");
        var listItem = Assert.Single(listed, i => i.Id == issue.Id);
        Assert.Equal("mohist/pr", listItem.WorkflowProfileId);

        var profileResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
        var profile = await profileResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mohist/pr", profile.GetProperty("data").GetProperty("profileId").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithoutWorkflowProfile_InheritsDefaultOnAllReadSurfaces()
    {
        var project = await CreateProjectAsync("wfp-create-default");

        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "No profile", projectId = project.Id });

        // Stored selection is null → reads fall back to system default.
        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/default", detail.WorkflowProfileId);

        var profile = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var profileData = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("mohist/default", profileData.GetProperty("profileId").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithUnknownWorkflowProfile_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync("wfp-create-unknown");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new { title = "Bad profile", projectId = project.Id, workflowProfileId = "team/missing" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_workflow_profile", body.GetProperty("code").GetString());
        Assert.Contains("team/missing", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_ReplacesWorkflowProfile_WhenPresent()
    {
        var project = await CreateProjectAsync("wfp-patch-replace");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Switch profile", projectId = project.Id });

        Assert.Equal("mohist/default", issue.WorkflowProfileId);

        var patched = await _client.PatchDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { workflowProfileId = "mohist/pr" });

        Assert.Equal("mohist/pr", patched.WorkflowProfileId);

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/pr", detail.WorkflowProfileId);

        var listed = await _client.GetDataAsync<IssueDto[]>($"/api/projects/{project.Id}/issues?all=true");
        var listItem = Assert.Single(listed, i => i.Id == issue.Id);
        Assert.Equal("mohist/pr", listItem.WorkflowProfileId);

        var profile = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var profileData = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("mohist/pr", profileData.GetProperty("profileId").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_WithoutWorkflowProfile_PreservesExistingSelection()
    {
        var project = await CreateProjectAsync("wfp-patch-keep");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Keep profile", projectId = project.Id, workflowProfileId = "mohist/pr" });

        var patched = await _client.PatchDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { body = "Renamed body" });

        Assert.Equal("mohist/pr", patched.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_NullWorkflowProfile_ClearsSelectionAndReadsInheritDefault()
    {
        var project = await CreateProjectAsync("wfp-patch-clear");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Clear profile", projectId = project.Id, workflowProfileId = "mohist/pr" });

        var patched = await _client.PatchDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { workflowProfileId = (string?)null });

        // Present-and-null clears the issue-level selection; reads fall back
        // to the inherited default.
        Assert.Equal("mohist/default", patched.WorkflowProfileId);

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/default", detail.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_WithUnknownWorkflowProfile_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync("wfp-patch-unknown");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Bad patch", projectId = project.Id });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { workflowProfileId = "team/missing" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_workflow_profile", body.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_ProfileOnStartedIssue_ReturnsConflictAndLeavesSelectionUnchanged()
    {
        var project = await CreateProjectAsync("wfp-patch-locked");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Started issue", projectId = project.Id, workflowProfileId = "mohist/pr", isDraft = false });

        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(
            project.Id, "wfp-patch-locked", RepositoryBaseBranch: "main"));
        _startedProjectId = project.Id;
        _startedIssueNumber = issue.Number;

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { workflowProfileId = "mohist/default" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workflow_profile_locked", body.GetProperty("code").GetString());
        Assert.Contains(wrId, body.GetProperty("error").GetString(), StringComparison.Ordinal);

        // Selection must be unchanged after the rejected PATCH.
        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/pr", detail.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PutVariables_OnStartedIssue_StillSucceeds()
    {
        var project = await CreateProjectAsync("wfp-vars-started");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Variables on started", projectId = project.Id, isDraft = false });

        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        await grain.StartWorkAsync(new WorkflowProjectContext(
            project.Id, "wfp-vars-started", RepositoryBaseBranch: "main"));
        _startedProjectId = project.Id;
        _startedIssueNumber = issue.Number;

        // The variable endpoint is a run-scoped runtime override. It must
        // still succeed on a started issue; the rejection guard is on
        // PATCH workflowProfileId only.
        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/variables",
            new
            {
                vars = new Dictionary<string, object?>
                {
                    ["agent"] = new { type = "opencode" },
                },
                stages = new Dictionary<string, object?>(),
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_ProfileUpdate_PreservesConfiguredVariablesAndPrompts()
    {
        var project = await CreateProjectAsync("wfp-patch-preserve");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Preserve overlay", projectId = project.Id });

        // Seed variables and a prompt through the dedicated runtime
        // override endpoints so we can verify the PATCH profile update
        // does not touch them.
        await _client.PutAsJsonOkAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/variables",
            new
            {
                vars = new Dictionary<string, object?>
                {
                    ["agent"] = new { type = "opencode" },
                    ["modelVariant"] = "max",
                },
                stages = new Dictionary<string, object?>(),
            });
        await _client.PutAsJsonOkAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/prompts/plan",
            new { body = "PLAN_PROMPT_BODY" });

        var beforeProfile = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        var beforeData = (await beforeProfile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var beforeVariables = beforeData.GetProperty("variables");
        var beforeVarsAgent = beforeVariables.GetProperty("vars").GetProperty("agent").GetProperty("type").GetString();
        Assert.Equal("opencode", beforeVarsAgent);

        var patched = await _client.PatchDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { workflowProfileId = "mohist/pr" });

        Assert.Equal("mohist/pr", patched.WorkflowProfileId);

        var afterProfile = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        var afterData = (await afterProfile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var afterVariables = afterData.GetProperty("variables");
        Assert.Equal("mohist/pr", afterData.GetProperty("profileId").GetString());
        // The configured runtime overlay must survive a profile selection
        // change — PATCH workflowProfileId touches the issue aggregate's
        // selection only, not the variable bundle.
        Assert.Equal("opencode", afterVariables.GetProperty("vars").GetProperty("agent").GetProperty("type").GetString());
        Assert.Equal("max", afterVariables.GetProperty("vars").GetProperty("modelVariant").GetString());

        var prompts = await _client.GetDataAsync<Dictionary<string, string>>(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/prompts");
        Assert.Equal("PLAN_PROMPT_BODY", prompts["plan"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkflow_WithPrProfile_UsesPrSystemTemplate()
    {
        var project = await CreateProjectAsync("wfp-start-pr");

        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "PR startup",
                projectId = project.Id,
                workflowProfileId = "mohist/pr",
                isDraft = false,
            });

        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(
            project.Id, "wfp-start-pr", RepositoryBaseBranch: "main"));
        _startedProjectId = project.Id;
        _startedIssueNumber = issue.Number;

        var yamlResponse = await _client.GetAsync($"/api/workflow-runs/{wrId}/yaml");
        Assert.Equal(HttpStatusCode.OK, yamlResponse.StatusCode);
        var yamlBody = await yamlResponse.Content.ReadFromJsonAsync<JsonElement>();
        var yaml = yamlBody.GetProperty("data").GetProperty("yaml").GetString();

        Assert.NotNull(yaml);
        Assert.Contains("integrate:merge-pr", yaml!, StringComparison.Ordinal);
        Assert.Contains("mohist/merge-pull-request", yaml!, StringComparison.Ordinal);
        Assert.DoesNotContain("integrate:rebase", yaml!, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkflow_WithDefaultProfile_UsesDefaultSystemTemplate()
    {
        var project = await CreateProjectAsync("wfp-start-default");

        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Default startup",
                projectId = project.Id,
                workflowProfileId = "mohist/default",
                isDraft = false,
            });

        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(
            project.Id, "wfp-start-default", RepositoryBaseBranch: "main"));
        _startedProjectId = project.Id;
        _startedIssueNumber = issue.Number;

        var yamlResponse = await _client.GetAsync($"/api/workflow-runs/{wrId}/yaml");
        Assert.Equal(HttpStatusCode.OK, yamlResponse.StatusCode);
        var yamlBody = await yamlResponse.Content.ReadFromJsonAsync<JsonElement>();
        var yaml = yamlBody.GetProperty("data").GetProperty("yaml").GetString();

        Assert.NotNull(yaml);
        Assert.Contains("integrate:rebase", yaml!, StringComparison.Ordinal);
        Assert.Contains("mohist/rebase", yaml!, StringComparison.Ordinal);
        Assert.DoesNotContain("integrate:open-pr", yaml!, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowProfileEndpoint_AgreesWithEffectiveProfile_ForPrIssue()
    {
        var project = await CreateProjectAsync("wfp-endpoint-pr-agree");

        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Endpoint agreement",
                projectId = project.Id,
                workflowProfileId = "mohist/pr",
            });

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        var listed = await _client.GetDataAsync<IssueDto[]>($"/api/projects/{project.Id}/issues?all=true");
        var listItem = Assert.Single(listed, i => i.Id == issue.Id);
        var profileResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        var profileData = (await profileResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        // GET /api/issues/:n, the list endpoint, and the workflow-profile
        // endpoint MUST all report the same effective profile id.
        Assert.Equal("mohist/pr", detail.WorkflowProfileId);
        Assert.Equal("mohist/pr", listItem.WorkflowProfileId);
        Assert.Equal("mohist/pr", profileData.GetProperty("profileId").GetString());

        // hasCustomTemplate is false — the issue has no advanced override;
        // the displayed selection IS the effective profile.
        Assert.False(profileData.GetProperty("hasCustomTemplate").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkflow_WithCustomYamlOverride_TakesPrecedenceOverPrProfile()
    {
        var project = await CreateProjectAsync("wfp-start-override");

        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Override startup",
                projectId = project.Id,
                workflowProfileId = "mohist/pr",
                isDraft = false,
            });

        var customYaml = """
            id: advanced-override
            stages:
              - stage: only-stage
                tasks:
                  - id: only-task
                    title: Custom override task
                    uses: spec/task
                    with:
                      prompt: Custom override prompt
                checks: []
            """;
        using var putResponse = await _client.PutAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template",
            new { yaml = customYaml });
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(
            project.Id, "wfp-start-override", RepositoryBaseBranch: "main"));
        _startedProjectId = project.Id;
        _startedIssueNumber = issue.Number;

        var yamlResponse = await _client.GetAsync($"/api/workflow-runs/{wrId}/yaml");
        var yamlBody = await yamlResponse.Content.ReadFromJsonAsync<JsonElement>();
        var yaml = yamlBody.GetProperty("data").GetProperty("yaml").GetString();
        Assert.NotNull(yaml);
        Assert.Contains("advanced-override", yaml!, StringComparison.Ordinal);
        Assert.Contains("only-task", yaml!, StringComparison.Ordinal);

        // The displayed selection is still the PR profile — the override
        // does NOT rewrite the displayed profile id; it is surfaced via
        // HasCustomTemplate / TemplateSource instead.
        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/pr", detail.WorkflowProfileId);
        var profileResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        var profileData = (await profileResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("mohist/pr", profileData.GetProperty("profileId").GetString());
        Assert.True(profileData.GetProperty("hasCustomTemplate").GetBoolean());
        Assert.Equal("custom", profileData.GetProperty("templateSource").GetString());
    }

    private async Task<ProjectDto> CreateProjectAsync(string prefix)
    {
        var project = await _client.PostDataAsync<ProjectDto>(
            "/api/projects",
            new { name = $"wfp-{prefix}-{Guid.NewGuid():N}" });
        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/repositories",
            new
            {
                name = "main",
                gitUrl = $"file://{Guid.NewGuid():N}",
                baseBranch = "main",
                isDefault = true,
            });
        return project;
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number, string Id, string WorkflowProfileId);
}
