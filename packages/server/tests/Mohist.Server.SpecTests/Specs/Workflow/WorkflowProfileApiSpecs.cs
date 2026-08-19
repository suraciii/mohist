using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow;

[Collection("RunnerMutationIntegration")]
public class WorkflowProfileApiSpecs
{
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;

    public WorkflowProfileApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _grains = fixture.Grains;
    }

    [Fact]
    public async Task PostMalformedYaml_ReturnsDefinitionValidationAndDoesNotPersist()
    {
        var project = await CreateProjectAsync();
        using var response = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/workflow-profiles", new
        {
            profileId = "broken",
            name = "Broken",
            definitionSource = "stages: [",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workflow_profile_definition_validation", json.GetProperty("code").GetString());
        Assert.Contains(
            json.GetProperty("details").EnumerateArray(),
            error => string.Equals(error.GetProperty("source").GetString(), "definition", StringComparison.OrdinalIgnoreCase));

        var profiles = await _client.GetDataAsync<JsonElement>($"/api/projects/{project.Id}/workflow-profiles");
        Assert.DoesNotContain(profiles.EnumerateArray(), profile => profile.GetProperty("profileId").GetString() == "broken");
    }

    [Fact]
    public async Task ListAndGet_ExposeAgentRuntime()
    {
        var project = await CreateProjectAsync();

        var profiles = await _client.GetDataAsync<JsonElement>($"/api/projects/{project.Id}/workflow-profiles");
        var builtin = profiles.EnumerateArray()
            .Single(profile => profile.GetProperty("profileId").GetString() == "mohist/local");
        var detail = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/workflow-profiles/mohist%2Flocal");

        Assert.Equal(WorkflowProfileCatalog.Profile.Name, builtin.GetProperty("name").GetString());
        Assert.Equal(WorkflowProfileCatalog.Profile.Description, builtin.GetProperty("description").GetString());
        Assert.Equal("opencode", builtin.GetProperty("agentRuntime").GetString());
        Assert.Equal("opencode", detail.GetProperty("agentRuntime").GetString());
        Assert.Equal("mohist/local", detail.GetProperty("profileId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(detail.GetProperty("definitionSource").GetString()));
        var stage = detail.GetProperty("stages").EnumerateArray()
            .Single(candidate => candidate.GetProperty("stage").GetString() == "plan");
        Assert.Equal("plan", stage.GetProperty("stage").GetString());
        Assert.NotEmpty(stage.GetProperty("tasks").EnumerateArray());
    }

    [Fact]
    public async Task ActionCatalog_ExposesRunnerReportedAgentCapabilities()
    {
        var project = await CreateProjectAsync();
        var runnerId = $"workflow-profile-catalog-{Guid.NewGuid():N}";
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry(
                "mohist/pi",
                [],
                [],
                [],
                "Run a Pi agent turn",
                ["agent-turn"])],
            []);

        try
        {
            using var register = await _client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
            {
                capabilities = new[] { "spec/*" },
                hostname = "workflow-profile-catalog-spec",
                actionCatalog = catalog,
            });
            register.EnsureSuccessStatusCode();

            var actual = await _client.GetDataAsync<JsonElement>($"/api/projects/{project.Id}/actions");

            var action = Assert.Single(actual.GetProperty("actions").EnumerateArray());
            Assert.Equal("mohist/pi", action.GetProperty("name").GetString());
            Assert.Equal(["agent-turn"], action.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
        }
        finally
        {
            await _client.PostAsJsonAsync($"/api/runner/{runnerId}/unregister", new { });
        }
    }

    [Fact]
    public async Task PatchAgentAction_ValidatesPersistsAndProjectsRuntime()
    {
        var project = await CreateProjectAsync();
        var runnerId = $"workflow-profile-override-{Guid.NewGuid():N}";
        var catalog = new ActionCatalog(
            [
                new ActionCatalogEntry("mohist/opencode", [], [], [], Capabilities: ["agent-turn"]),
                new ActionCatalogEntry("mohist/pi", [], [], [], Capabilities: ["agent-turn"]),
            ],
            []);

        try
        {
            using var register = await _client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
            {
                capabilities = new[] { "spec/*" },
                hostname = "workflow-profile-override-spec",
                actionCatalog = catalog,
            });
            register.EnsureSuccessStatusCode();

            using var create = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/workflow-profiles", new
            {
                profileId = "delivery",
                name = "Delivery",
                definitionSource = """
                    agentAction: mohist/opencode
                    stages:
                      - stage: build
                        tasks:
                          - id: implement
                            uses: ${{ profile.agentAction }}
                        checks: []
                    """,
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            using var patch = await _client.PatchAsJsonAsync(
                $"/api/projects/{project.Id}/workflow-profiles/delivery",
                new { agentAction = "mohist/pi" });
            patch.EnsureSuccessStatusCode();

            var detail = await _client.GetDataAsync<JsonElement>(
                $"/api/projects/{project.Id}/workflow-profiles/delivery");
            Assert.Equal("mohist/pi", detail.GetProperty("agentAction").GetString());
            Assert.Equal("pi", detail.GetProperty("agentRuntime").GetString());

            var runId = $"profile-selection-{Guid.NewGuid():N}";
            var bound = await _grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(project.Id)
                .BindWorkflowRunAsync(
                    new WorkflowProfileCommandPayload.BindWorkflowRun(
                        project.Id,
                        runId,
                        IssueNumber: 42,
                        EpicNumber: null,
                        ExplicitProfileId: "delivery",
                        Metadata: new WorkflowRunMetadata(
                            "Custom profile run",
                            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
                            ProjectId: project.Id,
                            IssueNumber: 42)),
                    $"profile-selection:{runId}",
                    expectedRevision: null);
            Assert.True(bound.IsApplied);
            Assert.Equal("delivery", bound.Binding?.ProfileId);
            Assert.Equal("mohist/pi", bound.Binding?.AgentAction);

            var conflict = await _grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(project.Id)
                .BindWorkflowRunAsync(
                    new WorkflowProfileCommandPayload.BindWorkflowRun(
                        project.Id,
                        runId,
                        IssueNumber: 42,
                        EpicNumber: null,
                        ExplicitProfileId: "delivery",
                        Metadata: new WorkflowRunMetadata(
                            "Custom profile run",
                            new DateTimeOffset(2026, 8, 14, 0, 1, 0, TimeSpan.Zero),
                            ProjectId: project.Id,
                            IssueNumber: 42),
                        Workspace: new WorkspaceIdentity("/different/worktree", "different-branch")),
                    $"profile-selection-conflict:{runId}",
                    expectedRevision: null);
            Assert.Equal(WorkflowProfileReferenceResultCode.ConflictingRequest, conflict.Code);
        }
        finally
        {
            await _client.PostAsJsonAsync($"/api/runner/{runnerId}/unregister", new { });
        }
    }

    [Fact]
    public async Task PutMalformedYaml_ReturnsDefinitionValidationAndPreservesStoredProfile()
    {
        var project = await CreateProjectAsync();
        var valid = new
        {
            profileId = "editable",
            name = "Editable",
            definitionSource = "stages:\n  - stage: build\n    tasks: []\n    checks: []\n",
        };
        using var create = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/workflow-profiles", valid);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var response = await _client.PutAsJsonAsync($"/api/projects/{project.Id}/workflow-profiles/editable", new
        {
            profileId = "editable",
            name = "Editable",
            definitionSource = "stages: [",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workflow_profile_definition_validation", json.GetProperty("code").GetString());
        var stored = await _client.GetDataAsync<JsonElement>($"/api/projects/{project.Id}/workflow-profiles/editable");
        Assert.Contains("stage: build", stored.GetProperty("definitionSource").GetString());
    }

    [Fact]
    public async Task PutValidYaml_ReturnsSerializedValidationResultAndPersists()
    {
        var project = await CreateProjectAsync();
        using var create = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/workflow-profiles", new
        {
            profileId = "editable-valid",
            name = "Editable",
            definitionSource = "stages:\n  - stage: build\n    tasks: []\n    checks: []\n",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{project.Id}/workflow-profiles/editable-valid",
            new
            {
                name = "Updated",
                description = "Updated through the coordinator",
                definitionSource = "stages:\n  - stage: deliver\n    tasks: []\n    checks: []\n",
            });

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("editable-valid", json.GetProperty("data").GetProperty("profileId").GetString());
        Assert.Equal("Updated", json.GetProperty("data").GetProperty("name").GetString());
        var validation = json.GetProperty("validation");
        Assert.Empty(validation.GetProperty("definitionErrors").EnumerateArray());
        Assert.Empty(validation.GetProperty("actionErrors").EnumerateArray());
        var stored = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/workflow-profiles/editable-valid");
        Assert.Contains("stage: deliver", stored.GetProperty("definitionSource").GetString());
    }

    [Fact]
    public async Task PutInvalidAgentAction_ReturnsSerializedActionErrorsAndPreservesStoredProfile()
    {
        var project = await CreateProjectAsync();
        var runnerId = $"workflow-profile-put-validation-{Guid.NewGuid():N}";
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode", [], [], [], Capabilities: ["agent-turn"])],
            []);

        try
        {
            using var register = await _client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
            {
                capabilities = new[] { "spec/*" },
                hostname = "workflow-profile-put-validation-spec",
                actionCatalog = catalog,
            });
            register.EnsureSuccessStatusCode();

            using var create = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/workflow-profiles", new
            {
                profileId = "editable-agent",
                name = "Editable Agent",
                definitionSource = """
                    agentAction: mohist/opencode
                    stages:
                      - stage: build
                        tasks:
                          - id: implement
                            uses: ${{ profile.agentAction }}
                    """,
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            using var response = await _client.PutAsJsonAsync(
                $"/api/projects/{project.Id}/workflow-profiles/editable-agent",
                new
                {
                    name = "Editable Agent",
                    definitionSource = """
                        agentAction: mohist/ghost
                        stages:
                          - stage: build
                            tasks:
                              - id: implement
                                uses: ${{ profile.agentAction }}
                        """,
                });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var errors = json.GetProperty("details").GetProperty("actionErrors").EnumerateArray().ToArray();
            Assert.Contains(errors, error =>
                error.GetProperty("source").GetString() == "action"
                && error.GetProperty("message").GetString()!.Contains("mohist/ghost", StringComparison.Ordinal));
            var stored = await _client.GetDataAsync<JsonElement>(
                $"/api/projects/{project.Id}/workflow-profiles/editable-agent");
            Assert.Equal("mohist/opencode", stored.GetProperty("agentAction").GetString());
        }
        finally
        {
            await _client.PostAsJsonAsync($"/api/runner/{runnerId}/unregister", new { });
        }
    }

    [Fact]
    public async Task PutApprovalChange_RejectsActiveRunStructureChangeAndPreservesStoredProfile()
    {
        var project = await CreateProjectAsync();
        var runnerId = $"workflow-profile-approval-{Guid.NewGuid():N}";
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode", [], [], [], Capabilities: ["agent-turn"])],
            []);

        try
        {
            using var register = await _client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
            {
                capabilities = new[] { "spec/*" },
                hostname = "workflow-profile-approval-spec",
                actionCatalog = catalog,
            });
            register.EnsureSuccessStatusCode();

            const string profileId = "approval-structure";
            using var create = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/workflow-profiles", new
            {
                profileId,
                name = "Approval Structure",
                definitionSource = ApprovalProfile(requiresApproval: true),
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            var runId = $"approval-structure-{Guid.NewGuid():N}";
            var bound = await _grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(project.Id)
                .BindWorkflowRunAsync(
                    new WorkflowProfileCommandPayload.BindWorkflowRun(
                        project.Id,
                        runId,
                        IssueNumber: 42,
                        EpicNumber: null,
                        ExplicitProfileId: profileId,
                        Metadata: new WorkflowRunMetadata(
                            "Approval structure run",
                            new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
                            ProjectId: project.Id,
                            IssueNumber: 42)),
                    $"approval-structure:{runId}",
                    expectedRevision: null);
            Assert.True(bound.IsApplied);

            using var response = await _client.PutAsJsonAsync(
                $"/api/projects/{project.Id}/workflow-profiles/{profileId}",
                new
                {
                    name = "Approval Structure",
                    definitionSource = ApprovalProfile(requiresApproval: false),
                });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var errors = json.GetProperty("details").GetProperty("definitionErrors").EnumerateArray().ToArray();
            Assert.Contains(errors, error =>
                error.GetProperty("message").GetString()!.Contains(
                    "retain requiresApproval=true",
                    StringComparison.Ordinal));

            var stored = await _client.GetDataAsync<JsonElement>(
                $"/api/projects/{project.Id}/workflow-profiles/{profileId}");
            Assert.Contains("requiresApproval: true", stored.GetProperty("definitionSource").GetString());
        }
        finally
        {
            await _client.PostAsJsonAsync($"/api/runner/{runnerId}/unregister", new { });
        }
    }

    private static string ApprovalProfile(bool requiresApproval) => """
        agentAction: mohist/opencode
        approval:
          feedback:
            tasks:
              - id: apply-feedback
                uses: ${{ profile.agentAction }}
        stages:
          - stage: build
            requiresApproval: REQUIRES_APPROVAL
            tasks:
              - id: implement
                uses: ${{ profile.agentAction }}
            checks: []
        """.Replace(
            "REQUIRES_APPROVAL",
            requiresApproval.ToString().ToLowerInvariant(),
            StringComparison.Ordinal);

    private Task<ProjectInfo> CreateProjectAsync() =>
        _client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects",
            $"workflow-profile-api-{Guid.NewGuid():N}");
}
