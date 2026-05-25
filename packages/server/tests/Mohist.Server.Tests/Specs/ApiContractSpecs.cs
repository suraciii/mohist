using System.Net;
using System.Net.Http.Json;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class ApiContractSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public ApiContractSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("/api/projects/current")]
    [InlineData("/api/questions")]
    [InlineData("/api/questions/question-1")]
    [InlineData("/api/opencode/models")]
    [InlineData("/api/issues/1/agent-session")]
    [InlineData("/api/agent/session-status")]
    public async Task RemovedLegacyApi_WhenRequested_ReturnsNotFound(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/questions/question-1/reply")]
    [InlineData("/api/questions/question-1/expire")]
    [InlineData("/api/settings/system/rebuild")]
    [InlineData("/api/issues/1/messages")]
    public async Task RemovedLegacyApiPost_WhenRequested_ReturnsNotFound(string path)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
