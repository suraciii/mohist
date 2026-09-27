using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Api;

/// <summary>
/// Specs for the on-demand actual-binding read (issue #1099):
/// <c>GET /api/workflow-runs/{workflowRunId}/binding</c>.
///
/// Covers:
/// <list type="bullet">
///   <item><description>AC1: after the bound Profile is edited, the binding read
///   returns the original start-time definition while the Profile read returns
///   the new content; `--yaml` agrees with the binding, not the Profile.</description></item>
///   <item><description>AC2: after the Issue's next-start selection changes, the
///   run binding is unchanged and the selection is reported separately; the
///   read never starts or restarts work.</description></item>
///   <item><description>AC5: records without a snapshot (or with an unreadable
///   snapshot / undecodable state) report unavailability with a known reason,
///   preserve retained identity/status, and never fall back to the latest
///   Profile.</description></item>
///   <item><description>AC6: unknown runs answer <c>not_found</c> and callers
///   without credentials are denied; the two cases stay distinct.</description></item>
///   <item><description>AC7: the ordinary detail read stays concise — the
///   complete definition travels only through the on-demand reads.</description></item>
/// </list>
/// </summary>
[Trait("level", "L1")]
public class WorkflowRunBindingApiSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly WorkflowDefinition ProfileV1 = new(
    [
        new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
        new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
    ]);

    private static readonly WorkflowDefinition ProfileV2 = new(
    [
        new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
        new StageDefinition("review", [new("review", "Review", "spec/task")], []),
    ]);

    private readonly DefaultMohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    public WorkflowRunBindingApiSpecs(DefaultMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task GetBinding_AfterProfileEdit_ReturnsStarttimeDefinitionNotLatestProfile()
    {
        var (projectId, _, issueKey, _, wrId) = await SeedRunWithDefaultProfileAsync(ProfileV1);

        // Edit the bound Profile for future runs.
        await WorkflowApiTestSupport.SeedWorkflowProfileAsync(_connectionString, projectId, ProfileV2);

        var response = await _client.GetAsync($"/api/workflow-runs/{wrId}/binding");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var binding = await ReadDataAsync(response);

        // The binding keeps the start-time facts: identity and bound Profile.
        Assert.Equal(wrId, binding.GetProperty("workflowRunId").GetString());
        Assert.Equal(projectId, binding.GetProperty("projectId").GetString());
        Assert.Equal("spec/workflow", binding.GetProperty("workflowProfileId").GetString());

        // The definition is the retained run snapshot — the edit did not
        // reach it, and the content is complete rather than summarized.
        var definition = binding.GetProperty("definition");
        Assert.True(definition.GetProperty("available").GetBoolean());
        Assert.Equal("run-snapshot", definition.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, definition.GetProperty("reason").ValueKind);
        var stageIds = StageIds(definition.GetProperty("content"));
        Assert.Equal(new[] { "plan", "build" }, stageIds);

        // The Profile read answers with the new definition — the caller can
        // distinguish their identities and scopes.
        var profile = await ReadDataAsync(
            await _client.GetAsync($"/api/projects/{projectId}/workflow-profiles/spec%2Fworkflow"));
        Assert.Equal(new[] { "plan", "review" }, StageIdsFromSummaries(profile.GetProperty("stages")));

        // `mo run view --yaml` reads the same bound snapshot, not the edit.
        var yaml = await ReadDataAsync(await _client.GetAsync($"/api/workflow-runs/{wrId}/yaml"));
        var yamlText = yaml.GetProperty("yaml").GetString()!;
        Assert.Contains("compile", yamlText, StringComparison.Ordinal);
        Assert.DoesNotContain("review", yamlText, StringComparison.Ordinal);

        _ = issueKey;
    }

    [Fact]
    public async Task GetBinding_AfterNextStartSelectionChange_ReturnsOriginalRunBindingWithoutRestartingWork()
    {
        var (projectId, _, issueKey, issueNumber, wrId) = await SeedRunWithDefaultProfileAsync(ProfileV1);

        // Before any selection change the issue inherits the project default.
        var before = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.Equal("inherit", before!.WorkflowProfileMode);
        Assert.Equal("project-default", before.WorkflowProfileSource);

        // Terminalize the run reference so the selection lock releases, then
        // change the Issue's next-start selection.
        await _grains.GetGrain<IIssueGrain>(issueKey).CompleteWorkAsync(wrId);
        using var patch = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}",
            new { workflowProfileId = "mohist/local" });
        patch.EnsureSuccessStatusCode();

        var after = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.Equal("explicit", after!.WorkflowProfileMode);
        Assert.Equal("issue", after.WorkflowProfileSource);
        Assert.Equal("mohist/local", after.WorkflowProfileId);

        // The run binding still reports the original start-time facts.
        var binding = await ReadDataAsync(
            await _client.GetAsync($"/api/workflow-runs/{wrId}/binding"));
        Assert.Equal("spec/workflow", binding.GetProperty("workflowProfileId").GetString());
        Assert.Equal(
            new[] { "plan", "build" },
            StageIds(binding.GetProperty("definition").GetProperty("content")));

        // The binding read is a read: it created no second run for the Issue.
        await using var db = await NewDbAsync();
        Assert.Equal(1, await db.WorkflowRuns.CountAsync(row =>
            row.MetadataProjectId == projectId && row.IssueNumber == issueNumber));
    }

    [Fact]
    public async Task GetBinding_WithoutRetainedSnapshot_ReportsUnavailabilityAndKeepsIdentity()
    {
        var (projectId, _, _, issueNumber, _) = await SeedRunWithDefaultProfileAsync(ProfileV1);
        var wrId = await InsertHistoricalRunAsync(projectId, issueNumber, snapshotJson: null);

        var binding = await ReadDataAsync(
            await _client.GetAsync($"/api/workflow-runs/{wrId}/binding"));

        // Known identity and status survive the missing snapshot.
        Assert.Equal(wrId, binding.GetProperty("workflowRunId").GetString());
        Assert.Equal(projectId, binding.GetProperty("projectId").GetString());
        Assert.Equal(issueNumber, binding.GetProperty("issueNumber").GetInt32());
        Assert.Equal("stopped", binding.GetProperty("status").GetString());
        Assert.Equal("spec/workflow", binding.GetProperty("workflowProfileId").GetString());

        // The definition is explicitly unavailable with its reason; the
        // latest Profile (which exists and would resolve) is not substituted.
        var definition = binding.GetProperty("definition");
        Assert.False(definition.GetProperty("available").GetBoolean());
        Assert.Equal("no-snapshot", definition.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, definition.GetProperty("source").ValueKind);
        Assert.Equal(JsonValueKind.Null, definition.GetProperty("content").ValueKind);
        Assert.DoesNotContain("compile", binding.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetBinding_WithUnreadableSnapshot_ReportsUnreadableReason()
    {
        var (projectId, _, _, issueNumber, _) = await SeedRunWithDefaultProfileAsync(ProfileV1);
        var wrId = await InsertHistoricalRunAsync(projectId, issueNumber, snapshotJson: "{ not json");

        var binding = await ReadDataAsync(
            await _client.GetAsync($"/api/workflow-runs/{wrId}/binding"));

        Assert.Equal(wrId, binding.GetProperty("workflowRunId").GetString());
        Assert.Equal("spec/workflow", binding.GetProperty("workflowProfileId").GetString());
        var definition = binding.GetProperty("definition");
        Assert.False(definition.GetProperty("available").GetBoolean());
        Assert.Equal("unreadable-snapshot", definition.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, definition.GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task GetBinding_WithUndecodableRunState_ReportsRetainedRowIdentity()
    {
        var projectId = await SeedProjectOnlyAsync();
        var wrId = $"wr_{Guid.NewGuid():N}";

        await using (var db = await NewDbAsync())
        {
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = wrId,
                State = "{\"unexpected\":true}",
            });
            await db.SaveChangesAsync();
        }

        var binding = await ReadDataAsync(
            await _client.GetAsync($"/api/workflow-runs/{wrId}/binding"));

        Assert.Equal(wrId, binding.GetProperty("workflowRunId").GetString());
        var definition = binding.GetProperty("definition");
        Assert.False(definition.GetProperty("available").GetBoolean());
        Assert.Equal("unreadable-run-state", definition.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task GetBinding_OnUnknownWorkflowRun_Returns404()
    {
        var response = await _client.GetAsync("/api/workflow-runs/wr_does_not_exist/binding");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ReadJsonOptions);
        Assert.Equal("not_found", payload.GetProperty("code").GetString());
        Assert.Contains("wr_does_not_exist", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetBinding_WithoutCredentials_IsDenied()
    {
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync("/api/workflow-runs/wr_any/binding");

        // Missing credentials are denied before any run content is read;
        // the 401 stays distinct from the 404 a known run's reader gets.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DetailRead_StaysConcise_AndCarriesNoDefinition()
    {
        var (projectId, _, _, _, wrId) = await SeedRunWithDefaultProfileAsync(ProfileV1);

        var data = await ReadDataAsync(await _client.GetAsync($"/api/workflow-runs/{wrId}"));

        var keys = data.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();
        Assert.Equal(["issueRef", "status", "workflowProfileId"], keys);
        Assert.DoesNotContain("compile", data.GetRawText(), StringComparison.Ordinal);

        _ = projectId;
    }

    private async Task<(string projectId, string projectName, string issueKey, int issueNumber, string wrId)> SeedRunWithDefaultProfileAsync(
        WorkflowDefinition definition)
    {
        var (projectId, projectName) = await SeedProjectAsync();
        var (issueKey, issueNumber) = await WorkflowApiTestSupport.CreateIssueInBacklogAsync(_grains, projectId);
        await WorkflowApiTestSupport.SeedWorkflowProfileAsync(_connectionString, projectId, definition);
        var wrId = await _grains.GetGrain<IIssueGrain>(issueKey).StartWorkAsync();
        return (projectId, projectName, issueKey, issueNumber, wrId);
    }

    private async Task<string> SeedProjectOnlyAsync()
    {
        var (projectId, _) = await SeedProjectAsync();
        await WorkflowApiTestSupport.SeedWorkflowProfileAsync(_connectionString, projectId, ProfileV1);
        return projectId;
    }

    /// <summary>
    /// Inserts a historical run row the way pre-snapshot records exist:
    /// retained identity, Profile, and status, with no (or an unreadable)
    /// definition snapshot in its state.
    /// </summary>
    private async Task<string> InsertHistoricalRunAsync(
        string projectId,
        int issueNumber,
        string? snapshotJson)
    {
        var wrId = $"wr_{Guid.NewGuid():N}";
        var run = WorkflowRun.Create(
            wrId,
            ProfileV1,
            TestTime.UtcNow,
            new WorkflowRunMetadata("historical", TestTime.UtcNow, ProjectId: projectId, IssueNumber: issueNumber));
        run.WorkflowProfileId = "spec/workflow";
        run.Status = WorkflowRunStatus.Stopped;
        run.BoundWorkflowDefinitionJson = snapshotJson;

        await using var db = await NewDbAsync();
        var row = new WorkflowRunRow
        {
            WorkflowRunId = wrId,
            State = JSON.Serialize(run),
        };
        db.WorkflowRuns.Add(row);
        db.Entry(row).Property<long>("ETag").CurrentValue = 1;
        await db.SaveChangesAsync();
        return wrId;
    }

    private async Task<(string projectId, string projectName)> SeedProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var name = $"wr-binding-{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync(name, new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:test.git",
            BaseBranch = "main",
            IsDefault = true,
        }, "git diff --check");
        return (id, name);
    }

    private async Task<MohistDbContext> NewDbAsync()
    {
        var db = new MohistDbContext(new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options);
        await db.Database.OpenConnectionAsync();
        return db;
    }

    private async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ReadJsonOptions);
        Assert.True(payload.GetProperty("success").GetBoolean());
        return payload.GetProperty("data");
    }

    private async Task<Mohist.Server.Issue.Services.IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<Mohist.Server.Issue.Services.IssueQuerier>();
        return await querier.GetInfoAsync(projectId, number);
    }

    private static string[] StageIds(JsonElement content)
    {
        Assert.Equal(JsonValueKind.Object, content.ValueKind);
        return [.. content.GetProperty("stages")
            .EnumerateArray()
            .Select(stage => stage.GetProperty("stage").GetString())];
    }

    private static string[] StageIdsFromSummaries(JsonElement stages)
    {
        Assert.Equal(JsonValueKind.Array, stages.ValueKind);
        return [.. stages.EnumerateArray().Select(stage => stage.GetProperty("stage").GetString())];
    }
}
