using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Runner;

public class WorkResultSerializationSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void SystemTextJson_RoundTripsCapturedOutputs()
    {
        var outputs = new Dictionary<string, JsonElement>
        {
            ["openspecName"] = JsonSerializer.Deserialize<JsonElement>("\"issue-97\""),
            ["changeDir"] = JsonSerializer.Deserialize<JsonElement>("\"openspec/changes/issue-97\""),
        };
        var original = new WorkResult("completed", Output: "{}", CapturedOutputs: outputs);

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<WorkResult>(json, JsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal("completed", roundTripped!.Status);
        Assert.NotNull(roundTripped.CapturedOutputs);
        Assert.Equal(2, roundTripped.CapturedOutputs!.Count);
        Assert.Equal("issue-97", roundTripped.CapturedOutputs["openspecName"].GetString());
        Assert.Equal("openspec/changes/issue-97", roundTripped.CapturedOutputs["changeDir"].GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void SystemTextJson_MissingCapturedOutputs_DeserializesToNull()
    {
        var json = """{"status":"completed","output":"{}"}""";

        var result = JsonSerializer.Deserialize<WorkResult>(json, JsonOptions);

        Assert.NotNull(result);
        Assert.Null(result!.CapturedOutputs);
    }
}
