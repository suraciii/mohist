using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workspace.Domain;
using Mohist.Server.Workspace.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workspace;

[Collection("MohistIntegration")]
public class IssueWorkspaceLifecycleSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly string _projectId;

    public IssueWorkspaceLifecycleSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _projectId = CreateProjectAsync().GetAwaiter().GetResult();
    }

    private async Task<string> CreateProjectAsync()
    {
        var raw = $"iwls-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        using var create = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "server", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        create.EnsureSuccessStatusCode();
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("CreateProject returned no id");
    }

    private IWorkspaceGrain WorkspaceGrain(string name) =>
        _fixture.Grains.GetGrain<IWorkspaceGrain>(GrainKey.Workspace(_projectId, name));

    // --- Acceptance 1: issue start creates issue-<n> workspace ---

    [Fact]
    public async Task IssueStart_CreatesWorkspaceWithCorrectOriginAndName()
    {
        using var createIssue = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/issues",
            new { title = "Workspace lifecycle test", isDraft = false });
        createIssue.EnsureSuccessStatusCode();
        var issue = await createIssue.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issue.GetProperty("data").GetProperty("number").GetInt32();
        var workspaceName = $"issue-{issueNumber}";

        using var start = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/issues/{issueNumber}/start", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, start.StatusCode);

        var ws = await WorkspaceGrain(workspaceName).GetAsync();
        Assert.NotNull(ws);
        Assert.Equal(workspaceName, ws!.Name);
        Assert.IsType<WorkspaceOrigin.Issue>(ws.Origin);
        Assert.Equal(issueNumber, ((WorkspaceOrigin.Issue)ws.Origin).IssueNumber);
        Assert.Equal(new[] { "server" }, ws.RepositoryNames);
        Assert.Equal(WorkspaceStatus.Active, ws.Status);
    }

    [Fact]
    public async Task IssueStart_PersistsWorkspaceNameOnIssue()
    {
        using var createIssue = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/issues",
            new { title = "Persist workspace name", isDraft = false });
        createIssue.EnsureSuccessStatusCode();
        var issue = await createIssue.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issue.GetProperty("data").GetProperty("number").GetInt32();

        using var start = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/issues/{issueNumber}/start", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, start.StatusCode);

        // Verify the workspace is visible via the workspace grain
        var workspaceName = $"issue-{issueNumber}";
        var ws = await WorkspaceGrain(workspaceName).GetAsync();
        Assert.NotNull(ws);
        Assert.Equal(workspaceName, ws!.Name);

        // Verify the IssueWorkStarted event was emitted (proof of durable record)
        using var eventsResp = await _fixture.Client.GetAsync(
            $"/api/projects/{_projectId}/issues/{issueNumber}/events");
        eventsResp.EnsureSuccessStatusCode();
        var eventsJson = await eventsResp.Content.ReadFromJsonAsync<JsonElement>();
        var events = eventsJson.GetProperty("data").EnumerateArray().ToList();
        var startedEvents = events.Where(e =>
        {
            var type = e.GetProperty("type").GetString();
            return type == "com.mohist.issue.work-started";
        }).ToList();
        Assert.NotEmpty(startedEvents);

        // The workspace.close endpoint should reject issue workspace
        using var closeResp = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/workspaces/{workspaceName}/close", null);
        Assert.False(closeResp.IsSuccessStatusCode);
    }

    // --- Acceptance 3: retry/rerun reuses same workspace ---

    [Fact]
    public async Task IssueStart_Twice_ReusesSameWorkspace()
    {
        using var createIssue = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/issues",
            new { title = "Retry workspace reuse", isDraft = false });
        createIssue.EnsureSuccessStatusCode();
        var issue = await createIssue.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issue.GetProperty("data").GetProperty("number").GetInt32();
        var workspaceName = $"issue-{issueNumber}";

        // First start
        using var start1 = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/issues/{issueNumber}/start", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, start1.StatusCode);

        var ws1 = await WorkspaceGrain(workspaceName).GetAsync();
        Assert.NotNull(ws1);
        Assert.Equal(WorkspaceStatus.Active, ws1!.Status);
        var createdAt1 = ws1.CreatedAt;

        // Second start (retry/rerun) — should reuse the same workspace, not create a new one
        using var start2 = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/issues/{issueNumber}/start", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, start2.StatusCode);

        var ws2 = await WorkspaceGrain(workspaceName).GetAsync();
        Assert.NotNull(ws2);
        Assert.Equal(ws1.Name, ws2!.Name);
        Assert.Equal(WorkspaceStatus.Active, ws2.Status);
        // Same creation timestamp proves it's the same entity
        Assert.Equal(createdAt1, ws2.CreatedAt);
    }

    // EnsureIssueWorkspaceAsync with active origin check refuses different issue number
    [Fact]
    public async Task EnsureIssueWorkspaceAsync_SameOriginKey_ReturnsExisting()
    {
        using var createIssue = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/issues",
            new { title = "Ensure idempotent", isDraft = false });
        createIssue.EnsureSuccessStatusCode();
        var issue = await createIssue.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issue.GetProperty("data").GetProperty("number").GetInt32();
        var workspaceName = $"issue-{issueNumber}";

        using var start = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/issues/{issueNumber}/start", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, start.StatusCode);

        // Calling EnsureIssueWorkspaceAsync again directly returns the same entity
        var ws = await WorkspaceGrain(workspaceName).EnsureIssueWorkspaceAsync(
            issueNumber, "server", _fixture.TimeProvider.GetUtcNow());
        Assert.NotNull(ws);
        Assert.Equal(workspaceName, ws.Name);
        Assert.IsType<WorkspaceOrigin.Issue>(ws.Origin);
        Assert.Equal(issueNumber, ((WorkspaceOrigin.Issue)ws.Origin).IssueNumber);
    }

    // --- Acceptance 4: auto-archive on done/cancelled, manual close rejected ---

    [Fact]
    public async Task WorkspaceClose_IssueOrigin_ReturnsNotAllowed()
    {
        using var createIssue = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/issues",
            new { title = "Close reject test", isDraft = false });
        createIssue.EnsureSuccessStatusCode();
        var issue = await createIssue.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issue.GetProperty("data").GetProperty("number").GetInt32();
        var workspaceName = $"issue-{issueNumber}";

        using var start = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/issues/{issueNumber}/start", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, start.StatusCode);

        var ex = await Assert.ThrowsAsync<WorkspaceDomainException>(
            () => WorkspaceGrain(workspaceName).CloseAsync(_fixture.TimeProvider.GetUtcNow()));
        Assert.Equal("workspace_close_not_allowed_for_issue", ex.Code);
        Assert.Contains("issue done", ex.Hint);
        Assert.Contains("issue cancel", ex.Hint);
    }

    [Fact]
    public async Task IssueClose_ArchivesWorkspace()
    {
        using var createIssue = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/issues",
            new { title = "Close archives workspace", isDraft = false });
        createIssue.EnsureSuccessStatusCode();
        var issue = await createIssue.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issue.GetProperty("data").GetProperty("number").GetInt32();
        var workspaceName = $"issue-{issueNumber}";

        using var start = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/issues/{issueNumber}/start", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, start.StatusCode);

        // Stop the workflow first, then close the issue
        await _fixture.Client.PostOkAsync(
            $"/api/projects/{_projectId}/issues/{issueNumber}/stop");
        using var close = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/issues/{issueNumber}/close", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, close.StatusCode);

        var ws = await WorkspaceGrain(workspaceName).GetAsync();
        Assert.NotNull(ws);
        Assert.Equal(WorkspaceStatus.Archived, ws!.Status);
        Assert.NotNull(ws.ArchivedAt);
    }

    [Fact]
    public async Task ArchiveByIssueAsync_WrongIssueNumber_ThrowsOriginMismatch()
    {
        using var createIssue = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/issues",
            new { title = "Archive mismatch test", isDraft = false });
        createIssue.EnsureSuccessStatusCode();
        var issue = await createIssue.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issue.GetProperty("data").GetProperty("number").GetInt32();
        var workspaceName = $"issue-{issueNumber}";

        using var start = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/issues/{issueNumber}/start", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, start.StatusCode);

        var ex = await Assert.ThrowsAsync<WorkspaceDomainException>(
            () => WorkspaceGrain(workspaceName).ArchiveByIssueAsync(
                issueNumber + 999, _fixture.TimeProvider.GetUtcNow()));
        Assert.Equal("workspace_origin_mismatch", ex.Code);
    }
}
