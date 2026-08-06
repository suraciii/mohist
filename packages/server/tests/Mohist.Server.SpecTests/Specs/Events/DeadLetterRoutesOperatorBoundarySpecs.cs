using Microsoft.Extensions.Configuration;
using Mohist.Server.Api;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

[Collection("MohistDb")]
public sealed class DeadLetterRoutesOperatorBoundarySpecs
{
    [Theory]
    [InlineData("http://127.0.0.1:3456", true)]
    [InlineData("http://localhost:3456", true)]
    [InlineData("http://192.168.1.10:3456", false)]
    [InlineData("http://0.0.0.0:3456", false)]
    [InlineData("http://*:3456", false)]
    [InlineData("http://[::]:3456", false)]
    public void OperatorBoundary_MapsRoutesOnlyOnLoopbackListener(string url, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = url })
            .Build();

        Assert.Equal(expected, DeadLetterRoutes.UsesLoopbackOnlyListener(configuration));
    }
}
