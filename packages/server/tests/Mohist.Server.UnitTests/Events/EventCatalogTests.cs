using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

public class EventCatalogTests
{
    [Fact]
    public void All_ContainsEveryReverseDnsConstant()
    {
        foreach (var field in typeof(EventCatalog.ReverseDns).GetFields())
        {
            if (field.GetRawConstantValue() is string value)
                Assert.Contains(value, EventCatalog.All);
        }
    }

    [Fact]
    public void All_HasNoDuplicates()
    {
        Assert.Equal(EventCatalog.All.Count, EventCatalog.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TranscriptTypes_AreNotProtocolTypes()
    {
        Assert.DoesNotContain(EventCatalog.TranscriptTypes, EventCatalog.All.Contains);
    }
}
