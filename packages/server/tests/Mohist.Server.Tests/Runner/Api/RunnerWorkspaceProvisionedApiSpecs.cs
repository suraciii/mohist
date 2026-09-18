using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workspace.Domain;
using Mohist.Server.Workspace.Grains;
using Xunit;

namespace Mohist.Server.Tests.Runner.Api;

/// <summary>
/// Runner-scoped provisioning report for a named Workspace Home: the runner
/// tells the server the local directory it provisioned, and the server
/// records the home (first writer wins) so later dispatches bind to this
/// runner.
/// </summary>
[Trait("level", "L1")]
public sealed class RunnerWorkspaceProvisionedApiSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerWorkspaceProvisionedApiSpecs(DefaultMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Provisioned_ValidReport_RecordsHomeAndReturnsPath()
    {
        var (projectId, workspaceName) = await CreateProjectAndWorkspaceAsync("provisioned-ok");
        const string path = "/virtual/ws/provisioned";

        using var response = await PostProvisionedAsync("runner-1", projectId, workspaceName, path);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        Assert.Equal("runner-1", data.GetProperty("runnerId").GetString());
        Assert.Equal(path, data.GetProperty("path").GetString());

        var home = await HomeOfAsync(projectId, workspaceName);
        Assert.NotNull(home);
        Assert.Equal("runner-1", home!.RunnerId);
        Assert.Equal(path, home.Path);
    }

    [Fact]
    public async Task Provisioned_SecondRunnerForSameWorkspace_ReturnsHomeClaimed()
    {
        var (projectId, workspaceName) = await CreateProjectAndWorkspaceAsync("provisioned-claim");
        using (var first = await PostProvisionedAsync("runner-1", projectId, workspaceName, "/virtual/ws/first"))
        {
            first.EnsureSuccessStatusCode();
        }

        using var second = await PostProvisionedAsync("runner-2", projectId, workspaceName, "/virtual/ws/second");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var payload = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workspace_home_claimed", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Provisioned_BlankPath_ReturnsProvisioningInvalid()
    {
        var (projectId, workspaceName) = await CreateProjectAndWorkspaceAsync("provisioned-blank");

        using var response = await PostProvisionedAsync("runner-1", projectId, workspaceName, "   ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workspace_provisioning_invalid", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Provisioned_ArchivedWorkspace_ReturnsArchived()
    {
        var (projectId, workspaceName) = await CreateProjectAndWorkspaceAsync("provisioned-archived");
        using (var close = await _fixture.Client.PostAsync(
                   $"/api/projects/{projectId}/workspaces/{workspaceName}/close",
                   content: null))
        {
            close.EnsureSuccessStatusCode();
        }

        using var response = await PostProvisionedAsync("runner-1", projectId, workspaceName, "/virtual/ws/archived");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workspace_archived", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Provisioned_UnknownWorkspace_ReturnsNotFound()
    {
        using var response = await PostProvisionedAsync(
            "runner-1",
            "unknown-project",
            "never-created",
            "/virtual/ws/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", payload.GetProperty("code").GetString());
    }

    private Task<HttpResponseMessage> PostProvisionedAsync(
        string runnerId,
        string projectId,
        string workspaceName,
        string path) =>
        _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/workspaces/{projectId}/{workspaceName}/provisioned",
            new { path });

    private async Task<WorkspaceHome?> HomeOfAsync(string projectId, string workspaceName)
    {
        var grain = _fixture.Grains.GetGrain<IWorkspaceGrain>(GrainKey.Workspace(projectId, workspaceName));
        return await grain.GetHomeAsync();
    }

    private async Task<(string ProjectId, string WorkspaceName)> CreateProjectAndWorkspaceAsync(string prefix)
    {
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var projectName = raw.Length > 63 ? raw[..63] : raw;
        using var createProject = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name = projectName,
            verificationCommand = "true",
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        createProject.EnsureSuccessStatusCode();
        var projectBody = await createProject.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectBody.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("CreateProject returned no id");

        var workspaceName = $"ws-{Guid.NewGuid():N}";
        using var createWorkspace = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/workspaces",
            new { name = workspaceName, repositories = new[] { "main" } });
        createWorkspace.EnsureSuccessStatusCode();
        return (projectId, workspaceName);
    }
}
