using Mohist.Server.Events.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public sealed class EventDispatcherGrainKeyTests
{
    [Theory]
    [InlineData(EventDispatcherGrain.Global, true)]
    [InlineData("rogue", false)]
    [InlineData("__GLOBAL__", false)]
    [InlineData("", false)]
    public void IsGlobalKey_RecognizesOnlyTheCanonicalDispatcherKey(string key, bool expected)
    {
        Assert.Equal(expected, EventDispatcherGrain.IsGlobalKey(key));
    }
}
