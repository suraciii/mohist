using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Sessions;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.AgentOps;

/// <summary>
/// Calculation specs for <see cref="AgentUsageReporter.GetUsageTimeseriesAsync"/>,
/// the service behind <c>GET /api/projects/&#123;projectRef&#125;/agent/usage</c>.
/// These assert the timeseries structure the route surfaces (bucket count,
/// bucket ordering, and the day/week granularity chosen per window) without
/// an HTTP round-trip. The assertions are structural, so they do not depend
/// on the wall clock. The route contract (404 / 400 unknown range / accepted
/// ranges 200) stays in <c>AgentUsageTimeseriesApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class AgentUsageTimeseriesQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public AgentUsageTimeseriesQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private AgentUsageReporter CreateReporter() =>
        _fixture.Services.GetRequiredService<AgentUsageReporter>();

    [Fact]
    public async Task GetUsageTimeseriesAsync_DefaultWindow_ReturnsSevenOrderedDailyBuckets()
    {
        var reporter = CreateReporter();

        var result = await reporter.GetUsageTimeseriesAsync($"proj-{Guid.NewGuid():N}", windowDays: 7);

        Assert.NotEqual(default, result.RangeFrom);
        Assert.NotEqual(default, result.RangeTo);
        Assert.Equal("day", result.BucketGranularity);
        Assert.Equal(7, result.Buckets.Count);
        Assert.Equal(result.Buckets, result.Buckets.OrderBy(b => b.BucketStart).ToList());
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_WindowBeyondNinetyDays_UsesWeeklyBuckets()
    {
        var reporter = CreateReporter();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var daily7 = await reporter.GetUsageTimeseriesAsync(projectId, windowDays: 7);
        var daily30 = await reporter.GetUsageTimeseriesAsync(projectId, windowDays: 30);
        var weekly90 = await reporter.GetUsageTimeseriesAsync(projectId, windowDays: 90);

        Assert.Equal("day", daily7.BucketGranularity);
        Assert.Equal("day", daily30.BucketGranularity);
        Assert.Equal("week", weekly90.BucketGranularity);
    }
}
