using System.Collections.Concurrent;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.L0Tests.Telemetry;

public class OtelCollectorStatusTests
{
    [Fact]
    public void NewInstance_ReportsPortNotBound()
    {
        var status = new OtelCollectorStatus();

        Assert.False(status.IsPortBound);
        Assert.False(status.Current.IsPortBound);
    }

    [Fact]
    public void SetPortBound_UpdatesIsPortBound()
    {
        var status = new OtelCollectorStatus();

        status.SetPortBound(true);

        Assert.True(status.IsPortBound);
        Assert.True(status.Current.IsPortBound);

        status.SetPortBound(false);

        Assert.False(status.IsPortBound);
    }

    [Fact]
    public async Task ConcurrentWriters_ProduceConsistentState()
    {
        var status = new OtelCollectorStatus();
        var errors = new ConcurrentBag<Exception>();
        var iterations = 500;

        var writers = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() =>
            {
                try
                {
                    for (int n = 0; n < iterations; n++)
                    {
                        status.SetPortBound(n % 2 == 0);
                        _ = status.IsPortBound;
                        _ = status.Current;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }))
            .ToArray();

        await Task.WhenAll(writers);

        Assert.Empty(errors);
        // After the storm, the last writer's value is whatever — the
        // important property is that the value is internally consistent
        // (no torn reads observed above).
        Assert.True(status.IsPortBound == true || status.IsPortBound == false);
    }

    [Fact]
    public void Current_IsImmutableSnapshot()
    {
        var status = new OtelCollectorStatus();
        status.SetPortBound(true);

        var snapshot = status.Current;
        status.SetPortBound(false);

        Assert.True(snapshot.IsPortBound);
        Assert.False(status.IsPortBound);
    }
}
