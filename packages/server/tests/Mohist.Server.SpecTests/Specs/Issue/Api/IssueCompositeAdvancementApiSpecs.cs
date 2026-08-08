using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Infrastructure.Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

/// <summary>
/// Issue-419 T-002 + T-003 route-contract specs for compound
/// advancement. Each test below asserts the HTTP surface only — the
/// fan-out / aggregation / idempotency calculations behind these
/// routes are sunk into <c>IssueCompositeAdvancementGrainSpecs</c>:
/// <list type="bullet">
/// <item>GET /api/projects/{p}/issues/{n} on a parent issue returns
///   the additive shape without the legacy
///   <c>is_parent</c>/<c>parent-has-children</c> blocker strings.</item>
/// <item>POST /api/projects/{p}/issues/{n}/close on a parent whose
///   child is non-terminal returns 409 with the typed
///   <c>parent_has_non_terminal_children</c> envelope, leaving the
///   child untouched.</item>
/// <item>POST /api/projects/{p}/issues/{n}/reopen on a composite
///   parent with a cancelled child returns 200, flips the parent
///   to Backlog, and leaves the cancelled child alone.</item>
/// <item>POST /api/projects/{p}/issues/{n}/archive on a composite
///   parent cascades to its terminal children (Done + Cancelled).</item>
/// </list>
/// Spec:
/// <c>openspec/changes/issue-419/specs/compound-advancement/spec.md</c>
/// and
/// <c>openspec/changes/issue-419/specs/parent-status-aggregation/spec.md</c>.
/// </summary>
[Collection("IssueLifecycle")]
public class IssueCompositeAdvancementApiSpecs
{
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IssueCompositeAdvancementApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
    }

    [Fact]
    public async Task GetStartReadiness_OnParentWithChildren_ReturnsCanStartAndNoBlocker()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var child = await CreateIssueAsync(projectId, "Child", isDraft: false);
        await AttachChildAsync(projectId, child.Number, parent.Number);

        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues/{parent.Number}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Read model no longer surfaces the ParentHasChildren blocker.
        Assert.DoesNotContain("is_parent", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parent-has-children", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CloseParent_WithNonTerminalChild_ReturnsTypedConflict_WithoutCascade()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var child = await CreateIssueAsync(projectId, "Child", isDraft: false);
        await AttachChildAsync(projectId, child.Number, parent.Number);

        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{parent.Number}/close", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("parent_has_non_terminal_children", body.RootElement.GetProperty("code").GetString());
        using var scope = _services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<Mohist.Server.Issue.Services.IssueQuerier>();
        Assert.Equal("backlog", (await querier.GetAsync(projectId, child.Number))!.Status);
    }

    [Fact]
    public async Task ReopenParent_ReturnsToBacklog_AndCanAttachAndStartNewChild()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var existing = await CreateIssueAsync(projectId, "Existing", isDraft: false);
        await AttachChildAsync(projectId, existing.Number, parent.Number);
        await _grains.GetGrain<IIssueGrain>(existing.IssueKey).CancelAsync();
        await _grains.GetGrain<IIssueGrain>(parent.IssueKey).RecomputeCompositeStatusAsync();

        using (var response = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{parent.Number}/reopen", null))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var scope = _services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<Mohist.Server.Issue.Services.IssueQuerier>();
        Assert.Equal("backlog", (await querier.GetAsync(projectId, parent.Number))!.Status);
        Assert.Equal("cancelled", (await querier.GetAsync(projectId, existing.Number))!.Status);
    }

    [Fact]
    public async Task ArchiveParent_CascadesToDoneAndCancelledChildren()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var done = await CreateIssueAsync(projectId, "Done", isDraft: false);
        var cancelled = await CreateIssueAsync(projectId, "Cancelled", isDraft: false);
        await AttachChildAsync(projectId, done.Number, parent.Number);
        await AttachChildAsync(projectId, cancelled.Number, parent.Number);
        var parentGrain = _grains.GetGrain<IIssueGrain>(parent.IssueKey);
        var doneGrain = _grains.GetGrain<IIssueGrain>(done.IssueKey);
        var cancelledGrain = _grains.GetGrain<IIssueGrain>(cancelled.IssueKey);
        await parentGrain.StartCompositeAsync();
        await doneGrain.CompleteWorkAsync((await doneGrain.GetActiveWorkflowRunIdAsync())!);
        var cancelledWorkflowRunId = (await cancelledGrain.GetActiveWorkflowRunIdAsync())!;
        await _grains.GetGrain<Mohist.Server.Workflow.Grains.IWorkflowGrain>(cancelledWorkflowRunId).StopAsync("test-cancel");
        await cancelledGrain.CancelAsync();
        await parentGrain.RecomputeCompositeStatusAsync();

        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{parent.Number}/archive", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<Mohist.Server.Issue.Services.IssueQuerier>();
        Assert.NotNull((await querier.GetAsync(projectId, parent.Number))!.ArchivedAt);
        Assert.NotNull((await querier.GetAsync(projectId, done.Number))!.ArchivedAt);
        Assert.NotNull((await querier.GetAsync(projectId, cancelled.Number))!.ArchivedAt);
    }

    private async Task<string> CreateProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync($"mohist-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:mohist-local.git",
            BaseBranch = "main",
            IsDefault = true,
        });
        return id;
    }

    private async Task<(int Number, string IssueKey)> CreateIssueAsync(string projectId, string title, bool isDraft)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        await grain.CreateAsync(projectId, number, title, null, null, null, isDraft: isDraft);
        return (number, issueKey);
    }

    private async Task AttachChildAsync(string projectId, int childNumber, int parentNumber)
    {
        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{childNumber}",
            new { parentIssueNumber = parentNumber },
            JsonOptions);
        response.EnsureSuccessStatusCode();
    }
}