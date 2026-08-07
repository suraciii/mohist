using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Foundation;

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

}
