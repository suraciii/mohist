using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

[Collection("MohistIntegration")]
public class WorkflowRerunFromStageApiSpecs
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    public WorkflowRerunFromStageApiSpecs(MohistIntegrationFixture fixture)
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
    public async Task RerunFromStage_EmptyStage_Returns400()
    {
        var (projectId, issueNumber, _, _) = await SeedInProgressIssueWithWorkflowRunAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RerunFromStage_NoWorkflowRun_Returns404()
    {
        var (projectId, issueNumber, _) = await SeedProjectWithIssueOnlyAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "build" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RerunFromStage_UnknownStage_Returns400()
    {
        var (projectId, issueNumber, issueId, wrId) = await SeedInProgressIssueWithWorkflowRunAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "nonexistent" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_stage", payload.GetProperty("code").GetString());
        Assert.True(payload.TryGetProperty("details", out var details));
        Assert.True(details.TryGetProperty("eligibleStages", out var eligible));
        var stages = eligible.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("plan", stages);
    }

    private async Task<(string projectId, int issueNumber, string issueId, string wrId)>
        SeedInProgressIssueWithWorkflowRunAsync()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueId, issueNumber) = await CreateIssueInBacklogAsync(projectId);
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        var wrId = await grain.StartWorkAsync();
        return (projectId, issueNumber, issueId, wrId);
    }

    private async Task<(string projectId, int issueNumber, string issueId)>
        SeedProjectWithIssueOnlyAsync()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueId, issueNumber) = await CreateIssueInBacklogAsync(projectId);
        return (projectId, issueNumber, issueId);
    }

    private async Task<(string projectId, string projectName)> SeedProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var name = $"rerun-stage-{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync(name);
        await projectGrain.AddRepositoryAsync("origin", "git@example.com:test.git", "main");
        return (id, name);
    }

    private async Task<(string issueId, int number)> CreateIssueInBacklogAsync(string projectId)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        await grain.CreateAsync(projectId, number, "Rerun from stage test", null, null, null, null, issueId, isDraft: false);
        return (issueId, number);
    }
}
