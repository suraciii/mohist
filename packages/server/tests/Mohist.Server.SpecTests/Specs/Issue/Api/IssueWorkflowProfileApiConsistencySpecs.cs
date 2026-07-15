using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

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
[Collection("IntegrationIssue2")]
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
                workflowProfileId = "mohist/github-pr",
            });

        Assert.Equal("mohist/github-pr", issue.WorkflowProfileId);

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/github-pr", detail.WorkflowProfileId);

        var listed = await _client.GetDataAsync<IssueDto[]>($"/api/projects/{project.Id}/issues?all=true");
        var listItem = Assert.Single(listed, i => i.Id == issue.Id);
        Assert.Equal("mohist/github-pr", listItem.WorkflowProfileId);

        var profileResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
        var profile = await profileResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mohist/github-pr", profile.GetProperty("data").GetProperty("profileId").GetString());
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
        Assert.Equal("mohist/local", detail.WorkflowProfileId);

        var profile = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var profileData = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("mohist/local", profileData.GetProperty("profileId").GetString());
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

        Assert.Equal("mohist/local", issue.WorkflowProfileId);

        var patched = await _client.PatchDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { workflowProfileId = "mohist/github-pr" });

        Assert.Equal("mohist/github-pr", patched.WorkflowProfileId);

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/github-pr", detail.WorkflowProfileId);

        var listed = await _client.GetDataAsync<IssueDto[]>($"/api/projects/{project.Id}/issues?all=true");
        var listItem = Assert.Single(listed, i => i.Id == issue.Id);
        Assert.Equal("mohist/github-pr", listItem.WorkflowProfileId);

        var profile = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var profileData = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("mohist/github-pr", profileData.GetProperty("profileId").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_WithoutWorkflowProfile_PreservesExistingSelection()
    {
        var project = await CreateProjectAsync("wfp-patch-keep");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Keep profile", projectId = project.Id, workflowProfileId = "mohist/github-pr" });

        var patched = await _client.PatchDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { body = "Renamed body" });

        Assert.Equal("mohist/github-pr", patched.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_NullWorkflowProfile_ClearsSelectionAndReadsInheritDefault()
    {
        var project = await CreateProjectAsync("wfp-patch-clear");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Clear profile", projectId = project.Id, workflowProfileId = "mohist/github-pr" });

        var patched = await _client.PatchDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { workflowProfileId = (string?)null });

        // Present-and-null clears the issue-level selection; reads fall back
        // to the inherited default.
        Assert.Equal("mohist/local", patched.WorkflowProfileId);

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/local", detail.WorkflowProfileId);
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
            new { title = "Started issue", projectId = project.Id, workflowProfileId = "mohist/github-pr", isDraft = false });

        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(
            project.Id, "wfp-patch-locked", RepositoryBaseBranch: "main"));
        _startedProjectId = project.Id;
        _startedIssueNumber = issue.Number;

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { workflowProfileId = "mohist/local" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workflow_profile_locked", body.GetProperty("code").GetString());
        Assert.Contains(wrId, body.GetProperty("error").GetString(), StringComparison.Ordinal);

        // Selection must be unchanged after the rejected PATCH.
        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/github-pr", detail.WorkflowProfileId);
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
            new { workflowProfileId = "mohist/github-pr" });

        Assert.Equal("mohist/github-pr", patched.WorkflowProfileId);

        var afterProfile = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        var afterData = (await afterProfile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var afterVariables = afterData.GetProperty("variables");
        Assert.Equal("mohist/github-pr", afterData.GetProperty("profileId").GetString());
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
    public async Task GetWorkflowProfile_AfterStageVariableNullClear_DoesNotExposeInternalBookkeeping()
    {
        var project = await CreateProjectAsync("wfp-stage-clear-contract");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Stage clear contract", projectId = project.Id });

        await _client.PutAsJsonOkAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/variables",
            new
            {
                vars = new Dictionary<string, object?>
                {
                    ["foo"] = "bar",
                },
                stages = new Dictionary<string, object?>
                {
                    ["plan"] = new
                    {
                        vars = new Dictionary<string, object?>
                        {
                            ["baz"] = "qux",
                            ["keep"] = "yes",
                        },
                    },
                },
            });

        await _client.PatchOkAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/variables",
            new
            {
                stages = new Dictionary<string, object?>
                {
                    ["plan"] = new
                    {
                        vars = new Dictionary<string, object?>
                        {
                            ["baz"] = null,
                        },
                    },
                },
            });

        var response = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var data = JsonDocument.Parse(json).RootElement.GetProperty("data");
        var planVars = data.GetProperty("variables").GetProperty("stages").GetProperty("plan").GetProperty("vars");

        Assert.False(planVars.TryGetProperty("baz", out _));
        Assert.Equal("yes", planVars.GetProperty("keep").GetString());
        Assert.DoesNotContain("StagesClearedKeys", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stagesClearedKeys", json, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PromptPreview_WithoutVariablesBody_UsesStoredIssueVariables()
    {
        var project = await CreateProjectAsync("wfp-preview-vars");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Preview variables", projectId = project.Id });

        await _client.PutAsJsonOkAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/variables",
            new
            {
                vars = new Dictionary<string, object?>
                {
                    ["foo"] = "bar",
                },
            });
        await _client.PutAsJsonOkAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/prompts/plan_prompt",
            new { body = "Plan with ${{ foo }}." });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/prompts/plan_prompt/preview",
            new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        Assert.Equal("Plan with bar.", data.GetProperty("rendered").GetString());
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
                workflowProfileId = "mohist/github-pr",
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
        Assert.Contains("id: merge-pr", yaml!, StringComparison.Ordinal);
        Assert.Contains("mohist/merge-github-pr", yaml!, StringComparison.Ordinal);
        Assert.DoesNotContain("integrate:rebase", yaml!, StringComparison.Ordinal);
        Assert.DoesNotContain("integrate:merge-pr", yaml!, StringComparison.Ordinal);
        Assert.DoesNotContain("mohist/merge-pull-request", yaml!, StringComparison.Ordinal);
        Assert.DoesNotContain("mohist/create-pull-request", yaml!, StringComparison.Ordinal);
        Assert.Contains("open-draft-pr", yaml!, StringComparison.Ordinal);
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
                workflowProfileId = "mohist/local",
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
                workflowProfileId = "mohist/github-pr",
            });

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        var listed = await _client.GetDataAsync<IssueDto[]>($"/api/projects/{project.Id}/issues?all=true");
        var listItem = Assert.Single(listed, i => i.Id == issue.Id);
        var profileResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        var profileData = (await profileResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        // GET /api/issues/:n, the list endpoint, and the workflow-profile
        // endpoint MUST all report the same effective profile id.
        Assert.Equal("mohist/github-pr", detail.WorkflowProfileId);
        Assert.Equal("mohist/github-pr", listItem.WorkflowProfileId);
        Assert.Equal("mohist/github-pr", profileData.GetProperty("profileId").GetString());

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
                workflowProfileId = "mohist/github-pr",
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
        Assert.Equal("mohist/github-pr", detail.WorkflowProfileId);
        var profileResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        var profileData = (await profileResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("mohist/github-pr", profileData.GetProperty("profileId").GetString());
        Assert.True(profileData.GetProperty("hasCustomTemplate").GetBoolean());
        Assert.Equal("custom", profileData.GetProperty("templateSource").GetString());
    }

    // ===================== Enable/disable workflow profiles =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithDisabledWorkflowProfile_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync("wfp-create-disabled");

        // Disable mohist/github-pr
        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/workflow-profile/disable",
            new { profileId = "mohist/github-pr" });

        // Create issue with disabled profile should fail
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new { title = "Disabled profile", projectId = project.Id, workflowProfileId = "mohist/github-pr" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_workflow_profile", body.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithDisabledWorkflowProfileInDifferentCase_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync("wfp-create-disabled-case");

        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/workflow-profile/disable",
            new { profileId = "mohist/github-pr" });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new { title = "Disabled profile case", projectId = project.Id, workflowProfileId = "MOHIST/GITHUB-PR" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_workflow_profile", body.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_WithDisabledWorkflowProfile_ReturnsBadRequestAndLeavesSelectionUnchanged()
    {
        var project = await CreateProjectAsync("wfp-patch-disabled");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Patch disabled", projectId = project.Id, workflowProfileId = "mohist/local" });

        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/workflow-profile/disable",
            new { profileId = "mohist/github-pr" });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { workflowProfileId = "mohist/github-pr" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_workflow_profile", body.GetProperty("code").GetString());

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/local", detail.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_WithDisabledWorkflowProfileInDifferentCase_ReturnsBadRequestAndLeavesSelectionUnchanged()
    {
        var project = await CreateProjectAsync("wfp-patch-disabled-case");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Patch disabled case", projectId = project.Id, workflowProfileId = "mohist/local" });

        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/workflow-profile/disable",
            new { profileId = "mohist/github-pr" });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { workflowProfileId = "MOHIST/GITHUB-PR" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_workflow_profile", body.GetProperty("code").GetString());

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("mohist/local", detail.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WhenNoProfileEnabled_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync("wfp-no-enabled");

        // Directly set all profiles disabled via raw SQL
        // (the service layer enforces the last-enabled invariant, so we
        // bypass it to test the issue-creation pre-flight check).
        using (var scope = _fixture.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Mohist.Server.Infrastructure.Data.Db.MohistDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var existing = await db.ProjectWorkflowProfiles.FirstOrDefaultAsync(x => x.ProjectId == project.Id);
            if (existing is null)
            {
                db.ProjectWorkflowProfiles.Add(new Mohist.Server.Infrastructure.Data.Workflow.ProjectWorkflowProfile
                {
                    ProjectId = project.Id,
                    Variables = "{}",
                    DisabledWorkflowProfileIds = ["mohist/local", "mohist/github-pr"],
                    UpdatedAt = TestTime.UtcNow,
                });
            }
            else
            {
                existing.DisabledWorkflowProfileIds = ["mohist/local", "mohist/github-pr"];
                existing.UpdatedAt = TestTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }

        // Create issue without explicit profile should fail
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new { title = "No enabled", projectId = project.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_enabled_workflow_profile", body.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithExplicitProfile_WhenNoProfileEnabled_ReturnsNoEnabledWorkflowProfile()
    {
        var project = await CreateProjectAsync("wfp-no-enabled-explicit");

        using (var scope = _fixture.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var existing = await db.ProjectWorkflowProfiles.FirstOrDefaultAsync(x => x.ProjectId == project.Id);
            if (existing is null)
            {
                db.ProjectWorkflowProfiles.Add(new Mohist.Server.Infrastructure.Data.Workflow.ProjectWorkflowProfile
                {
                    ProjectId = project.Id,
                    Variables = "{}",
                    DisabledWorkflowProfileIds = ["mohist/local", "mohist/github-pr"],
                    UpdatedAt = TestTime.UtcNow,
                });
            }
            else
            {
                existing.DisabledWorkflowProfileIds = ["mohist/local", "mohist/github-pr"];
                existing.UpdatedAt = TestTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new { title = "No enabled explicit", projectId = project.Id, workflowProfileId = "mohist/local" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_enabled_workflow_profile", body.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ExistingIssue_WhenAllProfilesDisabled_ReadSurfacesReportUnresolvedAndStartFails()
    {
        var project = await CreateProjectAsync("wfp-existing-all-disabled");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Existing all disabled", projectId = project.Id, isDraft = false });

        using (var scope = _fixture.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<Mohist.Server.Infrastructure.Data.Db.MohistDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var row = await db.ProjectWorkflowProfiles.FirstOrDefaultAsync(x => x.ProjectId == project.Id);
            if (row is null)
            {
                db.ProjectWorkflowProfiles.Add(new Mohist.Server.Infrastructure.Data.Workflow.ProjectWorkflowProfile
                {
                    ProjectId = project.Id,
                    Variables = "{}",
                    DisabledWorkflowProfileIds = ["mohist/local", "mohist/github-pr"],
                    UpdatedAt = TestTime.UtcNow,
                });
            }
            else
            {
                row.DisabledWorkflowProfileIds = ["mohist/local", "mohist/github-pr"];
                row.UpdatedAt = TestTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }

        var detailResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detailData = (await detailResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(JsonValueKind.Null, detailData.GetProperty("workflowProfileId").ValueKind);

        var listed = await _client.GetAsync($"/api/projects/{project.Id}/issues?all=true");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var listItem = (await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, listItem.GetProperty("workflowProfileId").ValueKind);

        var profileResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
        var profileData = (await profileResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(JsonValueKind.Null, profileData.GetProperty("profileId").ValueKind);

        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.StartWorkAsync(new WorkflowProjectContext(
                project.Id, "wfp-existing-all-disabled", RepositoryBaseBranch: "main")));
        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DisableLastEnabledProfile_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync("wfp-last-enabled");

        // Disable one profile first
        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/workflow-profile/disable",
            new { profileId = "mohist/local" });

        // Disabling the last one should fail
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/workflow-profile/disable",
            new { profileId = "mohist/github-pr" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("last_enabled_workflow_profile", body.GetProperty("code").GetString());

        // Verify the blacklist is unchanged (only mohist/local is disabled)
        var profiles = await _client.GetDataAsync<SystemTemplateInfoDto[]>(
            $"/api/workflow-templates/system?project={project.Id}");
        Assert.Contains(profiles, p => p.Id == "mohist/github-pr");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DisableUnknownWorkflowProfile_ReturnsBadRequestAndLeavesBlacklistUnchanged()
    {
        var project = await CreateProjectAsync("wfp-disable-unknown");

        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/workflow-profile/disable",
            new { profileId = "mohist/local" });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/workflow-profile/disable",
            new { profileId = "does/not/exist" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_workflow_profile", body.GetProperty("code").GetString());

        var profiles = await _client.GetDataAsync<SystemTemplateInfoDto[]>(
            $"/api/workflow-templates/system?project={project.Id}");
        var onlyEnabled = Assert.Single(profiles);
        Assert.Equal("mohist/github-pr", onlyEnabled.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DiscoveryReflectsDisabledProfile()
    {
        var project = await CreateProjectAsync("wfp-discovery");

        // Both profiles visible initially
        var all = await _client.GetDataAsync<SystemTemplateInfoDto[]>(
            $"/api/workflow-templates/system?project={project.Id}");
        Assert.Equal(2, all.Length);

        // Disable mohist/local
        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/workflow-profile/disable",
            new { profileId = "mohist/local" });

        // Only mohist/github-pr should remain
        var filtered = await _client.GetDataAsync<SystemTemplateInfoDto[]>(
            $"/api/workflow-templates/system?project={project.Id}");
        var filteredId = Assert.Single(filtered);
        Assert.Equal("mohist/github-pr", filteredId.Id);

        // Re-enable it
        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/workflow-profile/enable",
            new { profileId = "mohist/local" });

        var restored = await _client.GetDataAsync<SystemTemplateInfoDto[]>(
            $"/api/workflow-templates/system?project={project.Id}");
        Assert.Equal(2, restored.Length);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DiscoveryWithoutProject_ReturnsFullCatalog()
    {
        var full = await _client.GetDataAsync<SystemTemplateInfoDto[]>(
            "/api/workflow-templates/system");

        Assert.Equal(2, full.Length);
        Assert.Contains(full, t => t.Id == "mohist/local");
        Assert.Contains(full, t => t.Id == "mohist/github-pr");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowProfilesDiscovery_WithProject_FiltersDisabled()
    {
        var project = await CreateProjectAsync("wfp-profiles-discovery");

        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/workflow-profile/disable",
            new { profileId = "mohist/github-pr" });

        var profiles = await _client.GetDataAsync<WorkflowProfileDescriptionDto[]>(
            $"/api/workflow-profiles?project={project.Id}");

        var profileIds = profiles.Select(p => p.Id).ToList();
        Assert.Contains("mohist/local", profileIds);
        Assert.DoesNotContain("mohist/github-pr", profileIds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Discovery_WithMixedCaseDisabledProfile_FiltersDisabled()
    {
        var project = await CreateProjectAsync("wfp-discovery-case");

        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/workflow-profile/disable",
            new { profileId = "MOHIST/GITHUB-PR" });

        var profiles = await _client.GetDataAsync<WorkflowProfileDescriptionDto[]>(
            $"/api/workflow-profiles?project={project.Id}");

        var profileIds = profiles.Select(p => p.Id).ToList();
        Assert.Contains("mohist/local", profileIds);
        Assert.DoesNotContain("mohist/github-pr", profileIds);
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
                setDefault = true,
            });
        return project;
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number, string Id, string? WorkflowProfileId);
    private sealed record SystemTemplateInfoDto(string Id, string Name, string Description, bool IsDefault);
    private sealed record WorkflowProfileDescriptionDto(string Id, string DisplayName, string Description);
}
