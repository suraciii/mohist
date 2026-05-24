using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class WebCompatibilitySpecs
{
    private readonly HttpClient _client;

    public WebCompatibilitySpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task Comments_RoundTripThroughIssueDetailShape()
    {
        await _client.PostOkAsync("/api/projects", new { name = $"web-compat-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Commented issue" });

        var comment = await _client.PostDataAsync<CommentDto>($"/api/issues/{issue.Number}/comments", new { body = "Looks good" });
        var detail = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}");

        Assert.Equal("Looks good", comment.Body);
        Assert.Contains(detail.Comments, c => c.Id == comment.Id && c.Body == "Looks good");
    }

    [Fact]
    public async Task Prerequisites_ProjectIntoStartEligibility()
    {
        await _client.PostOkAsync("/api/projects", new { name = $"web-prereq-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var prereq = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Prereq" });
        var dependent = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Dependent" });

        await _client.PostOkAsync($"/api/issues/{dependent.Number}/prerequisites", new { prerequisiteNumber = prereq.Number });
        var detail = await _client.GetDataAsync<IssueDto>($"/api/issues/{dependent.Number}");

        Assert.False(detail.StartEligibility.Startable);
        Assert.Contains(detail.Prerequisites, p => p.Number == prereq.Number && !p.Delivered);
    }

    [Fact]
    public async Task SettingsAndProviderCompatibilityEndpoints_DoNotReturnMissingRoutes()
    {
        await _client.PutAsJsonOkAsync("/api/log-level", new { level = "DEBUG" });
        await _client.PutAsJsonOkAsync("/api/agent-runtime", new { timeout = 900, maxConcurrent = 5 });
        await _client.PostOkAsync("/api/providers/test", new { apiKey = "sk-test-key" });
        await _client.PostOkAsync("/api/providers/custom-openai", new { apiKey = "sk-test-key", baseURL = "https://example.test" });

        var logLevel = await _client.GetDataAsync<LogLevelDto>("/api/log-level");
        var runtime = await _client.GetDataAsync<AgentRuntimeDto>("/api/agent-runtime");
        var providers = await _client.GetDataAsync<ProviderDto[]>("/api/providers");
        var system = await _client.GetDataAsync<SystemInfoDto>("/api/system/info");

        Assert.Equal("DEBUG", logLevel.Level);
        Assert.Equal(900, runtime.Timeout);
        Assert.Equal(5, runtime.MaxConcurrent);
        Assert.Contains(providers, p => p.Id == "custom-openai" && p.Configured);
        Assert.Equal("running", system.Server.Status);
    }

    [Fact]
    public async Task Epics_LinkIssueAndExposePrimaryEpic()
    {
        await _client.PostOkAsync("/api/projects", new { name = $"web-epic-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Epic issue" });
        var epic = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Runtime model", description = "Ship runtime", priority = "p1" });

        await _client.PostOkAsync($"/api/epics/{epic.Id}/issues", new { issueId = issue.Id });
        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/epics/{epic.Id}");
        var issueDetail = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}");

        Assert.Contains(detail.LinkedIssues, i => i.Id == issue.Id);
        Assert.Equal(epic.Id, issueDetail.PrimaryEpic?.Id);
    }

    private sealed record IssueDto(int Number, string Id, CommentDto[] Comments, PrerequisiteDto[] Prerequisites, StartEligibilityDto StartEligibility, PrimaryEpicDto? PrimaryEpic);
    private sealed record CommentDto(string Id, string Body);
    private sealed record PrerequisiteDto(int Number, bool Delivered);
    private sealed record StartEligibilityDto(bool Startable);
    private sealed record PrimaryEpicDto(string Id, string Title);
    private sealed record LogLevelDto(string Level);
    private sealed record AgentRuntimeDto(int Timeout, int MaxConcurrent);
    private sealed record ProviderDto(string Id, bool Configured);
    private sealed record SystemInfoDto(ServerInfoDto Server);
    private sealed record ServerInfoDto(string Status);
    private sealed record EpicDto(string Id);
    private sealed record EpicDetailDto(LinkedIssueDto[] LinkedIssues);
    private sealed record LinkedIssueDto(string Id);
}
