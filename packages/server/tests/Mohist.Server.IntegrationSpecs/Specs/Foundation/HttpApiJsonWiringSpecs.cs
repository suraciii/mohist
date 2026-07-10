using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;
using Mohist.Server.IntegrationSpecs.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.IntegrationSpecs.Specs.Foundation;

[Collection("IntegrationMisc")]
public class HttpApiJsonWiringSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public HttpApiJsonWiringSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private const string NonAsciiText = "修复中文乱码 — Issue #1";

    [Fact]
    public async Task GetIssue_WithChineseTitle_ResponseBodyContainsVerbatimCharacters()
    {
        var projectId = await CreateProjectAsync($"proj-{Guid.NewGuid():N}");
        await AddRepositoryAsync(projectId);

        const string title = NonAsciiText;
        var createResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = created.GetProperty("data").GetProperty("number").GetInt32();

        using var rawResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}");
        Assert.Equal(HttpStatusCode.OK, rawResponse.StatusCode);

        var rawBody = await rawResponse.Content.ReadAsStringAsync();
        Assert.Contains(title, rawBody);
        Assert.DoesNotContain("\\u4fee", rawBody);
        Assert.DoesNotContain("\\u590d", rawBody);
        Assert.DoesNotContain("\\u4e2d", rawBody);
    }

    [Fact]
    public async Task PostIssue_WithInvalidChineseLabel_ErrorResponseBodyContainsVerbatimCharacters()
    {
        var projectId = await CreateProjectAsync($"proj-{Guid.NewGuid():N}");
        await AddRepositoryAsync(projectId);

        var invalidLabelKey = "中文标签";
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "An issue with bad labels",
                labels = new Dictionary<string, string> { [invalidLabelKey] = "value" },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(invalidLabelKey, body);
        Assert.DoesNotContain("\\u4e2d\\u6587", body);
        Assert.DoesNotContain("\\u4e2d", body);
        Assert.DoesNotContain("\\u6807\\u7b7e", body);
    }

    [Fact]
    public void HttpJsonOptions_MatchesUnifiedFacadeBehavior()
    {
        var jsonOptions = _fixture.Services.GetRequiredService<IOptions<JsonOptions>>().Value;

        Assert.Same(JSON.Options.Encoder, jsonOptions.SerializerOptions.Encoder);
        Assert.Equal(JSON.Options.DefaultIgnoreCondition, jsonOptions.SerializerOptions.DefaultIgnoreCondition);
        Assert.Equal(JSON.Options.PropertyNameCaseInsensitive, jsonOptions.SerializerOptions.PropertyNameCaseInsensitive);
        foreach (var converter in JSON.Options.Converters)
        {
            Assert.Contains(jsonOptions.SerializerOptions.Converters, c => c == converter);
        }
    }

    [Fact]
    public void HttpJsonOptions_UsesFacadeFailureReasonConverter()
    {
        var jsonOptions = _fixture.Services.GetRequiredService<IOptions<JsonOptions>>().Value;

        var json = JsonSerializer.Serialize(
            new FailureDetails(FailureReason.ContextExhaustion, Stage: "check", Message: "中文"),
            jsonOptions.SerializerOptions);

        Assert.Equal(
            JSON.Serialize(new FailureDetails(FailureReason.ContextExhaustion, Stage: "check", Message: "中文")),
            json);
        Assert.Contains("\"reason\":\"ContextExhaustion\"", json);
        Assert.Contains("\"message\":\"中文\"", json);
    }

    [Fact]
    public void SignalRJsonHubProtocolOptions_PayloadSerializerOptionsIsUnifiedFacade()
    {
        var protocolOptions = _fixture.Services
            .GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value;

        Assert.Same(JSON.Options, protocolOptions.PayloadSerializerOptions);
        Assert.Same(JSON.Options.Encoder, protocolOptions.PayloadSerializerOptions.Encoder);
    }

    [Fact]
    public void JsonHubProtocol_WithUnifiedFacade_WritesNonAsciiPayloadVerbatim()
    {
        var protocolOptions = _fixture.Services
            .GetRequiredService<IOptions<JsonHubProtocolOptions>>();
        var protocol = new JsonHubProtocol(protocolOptions);

        var message = new InvocationMessage(
            target: "OnEvent",
            arguments: new object?[] { "issue.created", NonAsciiText },
            invocationId: "inv-1");

        var bytes = protocol.GetMessageBytes(message);
        var text = Encoding.UTF8.GetString(bytes.Span);

        Assert.Contains(NonAsciiText, text);
        Assert.DoesNotContain("\\u4fee", text);
        Assert.DoesNotContain("\\u4e2d", text);
        Assert.DoesNotContain("\\u6587", text);
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name });
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }

    private async Task AddRepositoryAsync(string projectId)
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repositories",
            new
            {
                name = "main",
                gitUrl = $"file://{Guid.NewGuid():N}",
                baseBranch = "main",
                isDefault = true,
            });
        response.EnsureSuccessStatusCode();
    }
}
