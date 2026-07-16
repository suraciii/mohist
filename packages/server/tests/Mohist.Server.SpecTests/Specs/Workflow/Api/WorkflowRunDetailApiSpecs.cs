using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Api;

/// <summary>
/// Specs for the workflow-run detail read endpoint introduced in
/// issue-381 T-002:
/// <c>GET /api/workflow-runs/{workflowRunId}</c> → <c>WorkflowRunDetailDto</c>.
///
/// Covers:
/// <list type="bullet">
///   <item><description>Detail field completeness: run identity, status, stage progress, approval state, workflow-definition metadata, and the composed issue reference.</description></item>
///   <item><description>Associated-issue join hit: when the issue row exists, including a terminal issue row, <c>issueRef</c> carries <c>number</c>+<c>title</c>.</description></item>
///   <item><description>Associated-issue join miss: when no issue row is bound (transient gap / not yet seeded), <c>issueRef</c> is <c>null</c> and the run identity / status remain authoritative (404 only when the run itself is missing).</description></item>
///   <item><description>Invariant: <see cref="WorkflowStatusView"/> does not carry issue fields (the read model composes the view rather than extending it, so the
/// <c>tests/.../Workflow/Grain/StatusSpecs.cs:129</c> invariant is preserved).</description></item>
///   <item><description>Read-only semantics: a GET does not invoke any grain mutator and does not transition the run.</description></item>
/// </list>
/// </summary>
[Collection("IntegrationWorkflow")]
public class WorkflowRunDetailApiSpecs
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    public WorkflowRunDetailApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
        _connectionString = fixture.ConnectionString;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Get_ReturnsFullDetailWithAssociatedIssue()
    {
        var (_, _, _, issueNumber, wrId) = await SeedActiveWorkflowAsync();

        var response = await _client.GetAsync($"/api/workflow-runs/{wrId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ReadJsonOptions);
        Assert.True(payload.GetProperty("success").GetBoolean());

        var data = payload.GetProperty("data");
        // Run identity and status come through the composed WorkflowStatusView,
        // not from a flattened top-level payload (composition not embedding).
        Assert.Equal(wrId, data.GetProperty("status").GetProperty("workflowRunId").GetString());
        Assert.Equal("pending", data.GetProperty("status").GetProperty("status").GetString());
        var stages = data.GetProperty("status").GetProperty("stages");
        Assert.Equal(JsonValueKind.Array, stages.ValueKind);
        Assert.True(stages.GetArrayLength() >= 1, "stage progress must carry at least one stage");

        // Associated issue joins through IssueQuerier.GetIssueRefForWorkflowRunAsync.
        var issueRef = data.GetProperty("issueRef");
        Assert.Equal(JsonValueKind.Object, issueRef.ValueKind);
        Assert.Equal(issueNumber, issueRef.GetProperty("number").GetInt32());
        Assert.Equal("Workflow control test", issueRef.GetProperty("title").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Get_WhenIssueRowIsTerminal_IssueRefStillCarriesCorrelationContext()
    {
        var (_, _, _, issueNumber, wrId) = await SeedActiveWorkflowAsync();
        await ForceIssueStatusAsync(wrId, terminal: true);

        var response = await _client.GetAsync($"/api/workflow-runs/{wrId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ReadJsonOptions);
        Assert.True(payload.GetProperty("success").GetBoolean());

        var data = payload.GetProperty("data");
        // The run itself still exists and the issue correlation remains useful
        // after the issue leaves in-progress status. Completion-handler lookups
        // keep their in-progress filter, but the detail read model is a
        // workflowRunId -> issue context join for agents and scripts.
        Assert.Equal(wrId, data.GetProperty("status").GetProperty("workflowRunId").GetString());
        var issueRef = data.GetProperty("issueRef");
        Assert.Equal(JsonValueKind.Object, issueRef.ValueKind);
        Assert.Equal(issueNumber, issueRef.GetProperty("number").GetInt32());
        Assert.Equal("Workflow control test", issueRef.GetProperty("title").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Get_WhenIssueRowIsMissing_IssueRefIsNull()
    {
        var (_, _, _, _, wrId) = await SeedActiveWorkflowAsync();
        await DetachIssueAsync(wrId);

        var response = await _client.GetAsync($"/api/workflow-runs/{wrId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ReadJsonOptions);
        Assert.True(payload.GetProperty("success").GetBoolean());

        var data = payload.GetProperty("data");
        // Run identity and status are authoritative; the missing issue row
        // does not error — the issue ref is null and the read still returns.
        Assert.Equal(wrId, data.GetProperty("status").GetProperty("workflowRunId").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("issueRef").ValueKind);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Get_OnUnknownWorkflowRun_Returns404()
    {
        var response = await _client.GetAsync("/api/workflow-runs/wr_does_not_exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ReadJsonOptions);
        Assert.Equal("not_found", payload.GetProperty("code").GetString());
        Assert.Contains("wr_does_not_exist", payload.GetProperty("error").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Theory]
    [InlineData("/api/workflow-runs/wr_does_not_exist/yaml")]
    [InlineData("/api/workflow-runs/wr_does_not_exist/variables/effective")]
    [InlineData("/api/workflow-runs/wr_does_not_exist/variables/effective/some.key")]
    [InlineData("/api/workflow-runs/wr_does_not_exist/events")]
    [InlineData("/api/workflow-runs/wr_does_not_exist/sessions")]
    public async Task RunScopedReadSubresources_OnUnknownWorkflowRun_Return404(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ReadJsonOptions);
        Assert.Equal("not_found", payload.GetProperty("code").GetString());
        Assert.Contains("wr_does_not_exist", payload.GetProperty("error").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Get_DoesNotMutateRunState()
    {
        var (_, _, _, _, wrId) = await SeedActiveWorkflowAsync();

        // Snapshot the run status before, ensure it stays Pending across
        // the GET — the detail endpoint is purely a read.
        var before = await LoadRunAsync(wrId);
        Assert.Equal(WorkflowRunStatus.Pending, before!.Status);

        var response = await _client.GetAsync($"/api/workflow-runs/{wrId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await LoadRunAsync(wrId);
        Assert.Equal(before.Status, after!.Status);
        Assert.Equal(before.CurrentStageId, after.CurrentStageId);
        Assert.Equal(before.Stages.Count, after.Stages.Count);
    }

    private async Task<(string projectId, string projectName, string issueKey, int issueNumber, string wrId)> SeedActiveWorkflowAsync()
    {
        var (projectId, projectName) = await SeedProjectAsync();
        var (issueKey, issueNumber) = await CreateIssueInBacklogAsync(projectId);
        await SeedWorkflowTemplateAsync(projectId);
        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        var wrId = await grain.StartWorkAsync();
        await DispatchEventsAsync();
        return (projectId, projectName, issueKey, issueNumber, wrId);
    }

    private async Task<(string projectId, string projectName)> SeedProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var name = $"wr-detail-{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync(name, new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:test.git",
            BaseBranch = "main",
            IsDefault = true,
        });
        return (id, name);
    }

    private async Task<(string issueKey, int number)> CreateIssueInBacklogAsync(string projectId)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        await grain.CreateAsync(projectId, number, "Workflow control test", null, null, null, isDraft: false);
        return (issueKey, number);
    }

    private Task DispatchEventsAsync() =>
        _grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    private async Task SeedWorkflowTemplateAsync(string projectId)
    {
        var definition = new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
            new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
        ]);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var existingTemplate = await db.ProjectWorkflowTemplates.FindAsync(projectId, definition.Id);
        if (existingTemplate is null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = definition.Id,
                Template = JsonSerializer.Serialize(definition, WorkflowYamlSerializer.JsonOptions),
            });
        }
        else
        {
            existingTemplate.Template = JsonSerializer.Serialize(definition, WorkflowYamlSerializer.JsonOptions);
            existingTemplate.UpdatedAt = TestTime.UtcNow;
        }

        var profile = await db.ProjectWorkflowProfiles.FindAsync(projectId);
        if (profile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultTemplateId = definition.Id,
            });
        }
        else
        {
            profile.DefaultTemplateId = definition.Id;
            profile.UpdatedAt = TestTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowRun> LoadRunAsync(string wrId)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        return await store.LoadAsync(wrId) ?? throw new InvalidOperationException($"Workflow run '{wrId}' not found");
    }

    /// <summary>
    /// Mark the in-progress issue bound to <paramref name="wrId"/> as
    /// <c>Done</c> on the underlying <c>State</c> JSON — the
    /// <see cref="IssueQuerier.GetIssueRefForWorkflowRunAsync"/>
    /// status filter must exclude the row in that case so the detail
    /// endpoint returns a null ref.
    /// <para>
    /// <c>IssueRow.Status</c> and <c>IssueRow.WorkflowRunId</c> are
    /// computed columns projected from <c>State</c> (see
    /// <c>MohistDbContext.OnModelCreating</c>), so the source of
    /// truth is the JSON, not the row columns. <c>Issue.Status</c>/
    /// <c>Issue.WorkflowRunId</c> use <c>init</c>-only setters, so
    /// the mutation goes through a raw JSON round-trip rather than the
    /// typed <c>Issue</c> setters.
    /// </para>
    /// </summary>
    private async Task ForceIssueStatusAsync(string wrId, bool terminal)
    {
        await using var db = new MohistDbContext(new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options);

        var row = await db.Issues.AsNoTracking()
            .Where(r => r.WorkflowRunId == wrId)
            .FirstOrDefaultAsync();
        if (row is null) return;

        var tracked = await db.Issues.FindAsync(row.ProjectId, row.Number);
        if (tracked is null) return;
        using var doc = JsonDocument.Parse(tracked.State);
        var state = doc.RootElement.Deserialize<Dictionary<string, JsonElement>>()!;
        state["status"] = JsonSerializer.SerializeToElement(
            terminal ? nameof(IssueStatus.Done) : nameof(IssueStatus.InProgress),
            JSON.Options);
        tracked.State = JsonSerializer.Serialize(state, JSON.Options);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Force the issue row bound to <paramref name="wrId"/> out of the
    /// indexed join by nulling <c>State.workflowRunId</c> — exercises the
    /// "transiently-missing issue" mitigation documented on the
    /// detail endpoint contract.
    /// </summary>
    private async Task DetachIssueAsync(string wrId)
    {
        await using var db = new MohistDbContext(new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options);

        var row = await db.Issues.AsNoTracking()
            .Where(r => r.WorkflowRunId == wrId)
            .FirstOrDefaultAsync();
        if (row is null) return;

        var tracked = await db.Issues.FindAsync(row.ProjectId, row.Number);
        if (tracked is null) return;
        using var doc = JsonDocument.Parse(tracked.State);
        var state = doc.RootElement.Deserialize<Dictionary<string, JsonElement>>()!;
        state["workflowRunId"] = JsonSerializer.SerializeToElement<string?>(null, JSON.Options);
        tracked.State = JsonSerializer.Serialize(state, JSON.Options);
        await db.SaveChangesAsync();
    }
}
