using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Api;

[Trait("level", "L1")]
public sealed class WorkflowDiagnosisApiSpecs(DefaultMohistIntegrationFixture fixture) : IClassFixture<DefaultMohistIntegrationFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Get_ReturnsDiagnosisEnvelopeWithBoundedEvents()
    {
        var runId = await SeedActiveWorkflowAsync();

        using var response = await fixture.Client.GetAsync($"/api/runs/{runId}/diagnosis?limit=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(payload.GetProperty("success").GetBoolean());

        var data = payload.GetProperty("data");
        Assert.Equal(runId, data.GetProperty("workflowRunId").GetString());
        Assert.True(data.GetProperty("events").GetArrayLength() <= 1);
        Assert.DoesNotContain("/proc/", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/fd/", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_UnknownRun_ReturnsExisting404Envelope()
    {
        using var response = await fixture.Client.GetAsync("/api/runs/wr_does_not_exist/diagnosis");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("not_found", payload.GetProperty("code").GetString());
        Assert.Contains("wr_does_not_exist", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_RequiresOperatorScope()
    {
        var token = await CreatePatAsync("diagnosis-readonly", "readonly");
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync("/api/runs/wr_does_not_exist/diagnosis");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("forbidden", payload.GetProperty("code").GetString());
    }

    private async Task<string> SeedActiveWorkflowAsync()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var project = fixture.Grains.GetGrain<Mohist.Server.Project.Grains.IProjectGrain>(projectId);
        await project.CreateAsync(
            $"diagnosis-{Guid.NewGuid():N}",
            new Mohist.Server.Project.Domain.RepositoryInfo
            {
                Name = "origin",
                GitUrl = "git@example.com:test.git",
                BaseBranch = "main",
                IsDefault = true,
            },
            "git diff --check");

        var (issueKey, _) = await WorkflowApiTestSupport.CreateIssueInBacklogAsync(fixture.Grains, projectId);
        await WorkflowApiTestSupport.SeedWorkflowTemplateAsync(fixture.ConnectionString, projectId);
        var runId = await fixture.Grains.GetGrain<Mohist.Server.Issue.Grains.IIssueGrain>(issueKey).StartWorkAsync();
        return runId;
    }

    private async Task<string> CreatePatAsync(string name, string scope)
    {
        using var response = await fixture.Client.PostAsJsonAsync(
            "/api/auth/tokens",
            new { name, scope, ttlHours = 720 });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return payload.GetProperty("data").GetProperty("token").GetString()!;
    }
}
