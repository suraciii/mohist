using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Foundation;

public class HttpApiJsonWiringSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public HttpApiJsonWiringSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
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


}
