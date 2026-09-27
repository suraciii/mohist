using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Workflow;

[Collection("RunnerMutationIntegration")]
[Trait("level", "L1")]
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
    public async Task PostMalformedYaml_ReturnsUnifiedValidationAndDoesNotPersist()
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
        Assert.Equal("workflow_profile_validation", json.GetProperty("code").GetString());
        var details = json.GetProperty("details");
        var definitionErrors = details.GetProperty("definitionErrors").EnumerateArray().ToList();
        Assert.NotEmpty(definitionErrors);
        Assert.Contains(definitionErrors, error =>
            error.GetProperty("source").GetString() == "definition"
            && !string.IsNullOrEmpty(error.GetProperty("message").GetString()));
        Assert.Empty(details.GetProperty("actionErrors").EnumerateArray());

        var profiles = await _client.GetDataAsync<JsonElement>($"/api/projects/{project.Id}/workflow-profiles");
        Assert.DoesNotContain(profiles.EnumerateArray(), profile => profile.GetProperty("profileId").GetString() == "broken");
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
                processGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
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
        Assert.Equal("workflow_profile_validation", json.GetProperty("code").GetString());
        Assert.NotEmpty(json.GetProperty("details").GetProperty("definitionErrors").EnumerateArray());
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
    public async Task PostValidate_ReproductionInput_ReportsPathLocatedErrorsWithoutWriting()
    {
        var project = await CreateProjectAsync();
        var before = await _client.GetDataAsync<JsonElement>($"/api/projects/{project.Id}/workflow-profiles");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/workflow-profiles/validate",
            new { definitionSource = "stages:\n  - not-a-valid-stage: true\n" });

        response.EnsureSuccessStatusCode();
        var validation = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(project.Id, validation.GetProperty("projectId").GetString());
        var errors = validation.GetProperty("definitionErrors").EnumerateArray().ToList();
        Assert.NotEmpty(errors);
        Assert.Contains(errors, error =>
            error.GetProperty("path").GetString() == "stages[0].not-a-valid-stage"
            && error.GetProperty("message").GetString()!.Contains("not-a-valid-stage"));
    }

    [Fact]
    public async Task PostValidate_MissingActionInAvailableCatalog_ReportsPerformedActionErrorAndMatchesSave()
    {
        var project = await CreateProjectAsync();
        var runnerId = $"workflow-profile-validate-{Guid.NewGuid():N}";
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/pi", [], [], [], "Run a Pi agent turn", ["agent-turn"])],
            []);

        try
        {
            using var register = await _client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
            {
                processGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
                capabilities = new[] { "spec/*" },
                hostname = "workflow-profile-validate-spec",
                actionCatalog = catalog,
            });
            register.EnsureSuccessStatusCode();

            const string definition = "stages:\n  - stage: build\n    tasks:\n      - id: t\n        uses: mohist/ghost\n        with: {}\n    checks: []\n";
            using var validate = await _client.PostAsJsonAsync(
                $"/api/projects/{project.Id}/workflow-profiles/validate",
                new { definitionSource = definition });
            validate.EnsureSuccessStatusCode();
            var validation = (await validate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("performed", validation.GetProperty("actionValidationStatus").GetString(), ignoreCase: true);
            Assert.Empty(validation.GetProperty("definitionErrors").EnumerateArray());
            var actionErrors = validation.GetProperty("actionErrors").EnumerateArray().ToList();
            Assert.Contains(actionErrors, error => error.GetProperty("message").GetString()!.Contains("mohist/ghost"));

            // The save judges the same input and catalog context the same way
            // and writes nothing.
            using var save = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/workflow-profiles", new
            {
                profileId = "ghost-profile",
                name = "Ghost",
                definitionSource = definition,
            });
            Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
            var saveJson = await save.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("workflow_profile_validation", saveJson.GetProperty("code").GetString());
            var savedActionErrors = saveJson.GetProperty("details").GetProperty("actionErrors").EnumerateArray().ToList();
            Assert.Equal(actionErrors.Count, savedActionErrors.Count);

            var profiles = await _client.GetDataAsync<JsonElement>($"/api/projects/{project.Id}/workflow-profiles");
            Assert.DoesNotContain(profiles.EnumerateArray(), profile => profile.GetProperty("profileId").GetString() == "ghost-profile");
        }
        finally
        {
            await _client.PostAsJsonAsync($"/api/runner/{runnerId}/unregister", new { });
        }
    }

    [Fact]
    public async Task PostValidate_BlankDefinitionSource_IsABadRequest()
    {
        var project = await CreateProjectAsync();
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/workflow-profiles/validate",
            new { definitionSource = " " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("definition_source_required", json.GetProperty("code").GetString());
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
                processGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
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
        approval:
          feedback:
            tasks:
              - id: apply-feedback
                uses: mohist/agent
                with:
                  name: mohist/builder
                  prompt: Apply feedback.
        stages:
          - stage: build
            requiresApproval: REQUIRES_APPROVAL
            tasks:
              - id: implement
                uses: mohist/agent
                with:
                  name: mohist/builder
                  prompt: Implement.
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
