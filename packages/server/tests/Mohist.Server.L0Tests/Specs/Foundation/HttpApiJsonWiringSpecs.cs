using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Foundation;

public class HttpApiJsonWiringSpecs
{
    [Fact]
    public void HttpJsonOptions_MatchesUnifiedFacadeBehavior()
    {
        var jsonOptions = new JsonSerializerOptions();
        MohistServiceRegistration.CopyJsonOptions(JSON.Options, jsonOptions);

        Assert.Same(JSON.Options.Encoder, jsonOptions.Encoder);
        Assert.Equal(JSON.Options.DefaultIgnoreCondition, jsonOptions.DefaultIgnoreCondition);
        Assert.Equal(JSON.Options.PropertyNameCaseInsensitive, jsonOptions.PropertyNameCaseInsensitive);
        foreach (var converter in JSON.Options.Converters)
        {
            Assert.Contains(jsonOptions.Converters, c => c == converter);
        }
    }

    [Fact]
    public void HttpJsonOptions_UsesFacadeFailureReasonConverter()
    {
        var jsonOptions = new JsonSerializerOptions();
        MohistServiceRegistration.CopyJsonOptions(JSON.Options, jsonOptions);

        var json = JsonSerializer.Serialize(
            new FailureDetails(FailureReason.ContextExhaustion, Stage: "check", Message: "中文"),
            jsonOptions);

        Assert.Equal(
            JSON.Serialize(new FailureDetails(FailureReason.ContextExhaustion, Stage: "check", Message: "中文")),
            json);
        Assert.Contains("\"reason\":\"ContextExhaustion\"", json);
        Assert.Contains("\"message\":\"中文\"", json);
    }


}
