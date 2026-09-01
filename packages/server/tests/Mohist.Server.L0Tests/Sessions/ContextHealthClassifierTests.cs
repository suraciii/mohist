using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Sessions;

/// <summary>
/// Unit tests for <see cref="ContextHealthClassifier"/> covering the
/// traffic-light classification rules.
/// </summary>
public class ContextHealthClassifierTests
{
    [Theory]
    [InlineData(0d, ContextHealthClassifier.GreenStatus)]
    [InlineData(45d, ContextHealthClassifier.GreenStatus)]
    [InlineData(59.99d, ContextHealthClassifier.GreenStatus)]
    [InlineData(60d, ContextHealthClassifier.YellowStatus)]
    [InlineData(70d, ContextHealthClassifier.YellowStatus)]
    [InlineData(79.99d, ContextHealthClassifier.YellowStatus)]
    [InlineData(80d, ContextHealthClassifier.RedStatus)]
    [InlineData(85d, ContextHealthClassifier.RedStatus)]
    [InlineData(96d, ContextHealthClassifier.RedStatus)]
    [InlineData(100d, ContextHealthClassifier.RedStatus)]
    public void Classify_ReturnsExpectedStatusAtBoundaryAndBeyond(double percent, string expected)
    {
        Assert.Equal(expected, ContextHealthClassifier.Classify(percent));
    }

    [Fact]
    public void Classify_NullUsage_ReturnsNull()
    {
        Assert.Null(ContextHealthClassifier.Classify(null));
    }

    [Fact]
    public void ShouldEmitUpdate_CrossesGreenToYellow_Emits()
    {
        Assert.True(ContextHealthClassifier.ShouldEmitUpdate(
            previousStatus: ContextHealthClassifier.GreenStatus,
            previousPercent: 55d,
            nextPercent: 62d));
    }

    [Fact]
    public void ShouldEmitUpdate_CrossesYellowToRed_Emits()
    {
        Assert.True(ContextHealthClassifier.ShouldEmitUpdate(
            previousStatus: ContextHealthClassifier.YellowStatus,
            previousPercent: 75d,
            nextPercent: 82d));
    }

    [Fact]
    public void ShouldEmitUpdate_CrossesRedToGreenAfterCompaction_Emits()
    {
        Assert.True(ContextHealthClassifier.ShouldEmitUpdate(
            previousStatus: ContextHealthClassifier.RedStatus,
            previousPercent: 90d,
            nextPercent: 45d));
    }

    [Fact]
    public void ShouldEmitUpdate_SmallChangeBelowTenPoints_DoesNotEmit()
    {
        // No threshold crossing AND change < 10pp ⇒ no event.
        Assert.False(ContextHealthClassifier.ShouldEmitUpdate(
            previousStatus: ContextHealthClassifier.YellowStatus,
            previousPercent: 65d,
            nextPercent: 72d));
    }

    [Fact]
    public void ShouldEmitUpdate_SameStatusLargeJump_Emits()
    {
        // Stayed in red but jumped 15pp — emit a snapshot event.
        Assert.True(ContextHealthClassifier.ShouldEmitUpdate(
            previousStatus: ContextHealthClassifier.RedStatus,
            previousPercent: 81d,
            nextPercent: 96d));
    }

    [Fact]
    public void ShouldEmitUpdate_NullUsage_DoesNotEmit()
    {
        Assert.False(ContextHealthClassifier.ShouldEmitUpdate(
            previousStatus: ContextHealthClassifier.YellowStatus,
            previousPercent: 65d,
            nextPercent: null));
    }

    [Fact]
    public void ShouldEmitUpdate_FirstReadingWithNullPrevious_Emits()
    {
        // First health snapshot for the session should be emitted
        // so the UI can render the indicator even before any
        // threshold crossing.
        Assert.True(ContextHealthClassifier.ShouldEmitUpdate(
            previousStatus: null,
            previousPercent: null,
            nextPercent: 50d));
    }
}
