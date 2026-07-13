using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Foundation;

public class MohistServiceRegistrationJsonTests : IClassFixture<MohistDbFixture>
{
    private const string NonAsciiText = "修复中文乱码 — Issue #1";
    private readonly MohistDbFixture _fixture;

    public MohistServiceRegistrationJsonTests(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void HttpJsonOptions_CopySharedFacadeBehavior()
    {
        var jsonOptions = _fixture.Services.GetRequiredService<IOptions<JsonOptions>>().Value;

        Assert.Same(JSON.Options.Encoder, jsonOptions.SerializerOptions.Encoder);
        Assert.Equal(JSON.Options.DefaultIgnoreCondition, jsonOptions.SerializerOptions.DefaultIgnoreCondition);
        Assert.Equal(JSON.Options.PropertyNameCaseInsensitive, jsonOptions.SerializerOptions.PropertyNameCaseInsensitive);
        foreach (var converter in JSON.Options.Converters)
        {
            Assert.Contains(jsonOptions.SerializerOptions.Converters, candidate => candidate == converter);
        }
    }

    [Fact]
    public void HttpJsonOptions_SerializesFailureDetailsLikeFacade()
    {
        var jsonOptions = _fixture.Services.GetRequiredService<IOptions<JsonOptions>>().Value;
        var details = new FailureDetails(FailureReason.ContextExhaustion, Stage: "check", Message: "中文");

        var json = JsonSerializer.Serialize(details, jsonOptions.SerializerOptions);

        Assert.Equal(JSON.Serialize(details), json);
    }

    [Fact]
    public void SignalRJsonProtocol_UsesSharedFacade()
    {
        var protocolOptions = _fixture.Services
            .GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value;

        Assert.Same(JSON.Options, protocolOptions.PayloadSerializerOptions);
    }

    [Fact]
    public void SignalRJsonProtocol_WritesNonAsciiPayloadVerbatim()
    {
        var protocol = new JsonHubProtocol(_fixture.Services
            .GetRequiredService<IOptions<JsonHubProtocolOptions>>());
        var message = new InvocationMessage(
            target: "OnEvent",
            arguments: new object?[] { "issue.created", NonAsciiText },
            invocationId: "inv-1");

        var text = Encoding.UTF8.GetString(protocol.GetMessageBytes(message).Span);

        Assert.Contains(NonAsciiText, text);
        Assert.DoesNotContain("\\u4fee", text);
    }
}
