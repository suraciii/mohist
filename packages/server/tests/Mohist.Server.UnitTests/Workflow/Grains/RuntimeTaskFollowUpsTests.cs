using System.Text.Json;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grains;

public sealed class RuntimeTaskFollowUpsTests
{
    private static readonly RecoveryDefinition Recovery = new(
        2,
        [new RecoveryHandlerDefinition("output.promise=FAIL", [], RetrySelf: true)]);

    [Fact]
    public void Project_PreservesContinuationContract()
    {
        var projected = Assert.Single(RuntimeTaskFollowUps.Project([
            new RuntimeTaskInput(
                "review",
                "Review",
                "spec/review",
                With: JsonSerializer.SerializeToElement(new { options = "${{ vars.agent }}" }),
                Recovery: Recovery,
                RecoveryRemaining: 1,
                Expect: JsonSerializer.SerializeToElement(new
                {
                    markers = new[] { new { path = "review.md", failIf = "${{ vars.marker }}" } },
                }))
        ]));

        Assert.Equal("review", projected.Definition.Id);
        Assert.Equal("Review", projected.Definition.Title);
        Assert.Equal("spec/review", projected.Definition.Uses);
        Assert.Equal(1, projected.RecoveryRemaining);
        Assert.Equal(2, projected.Definition.Recovery!.Budget);
        Assert.Equal(
            "${{ vars.agent }}",
            projected.Definition.With!["options"]!.Value.GetString());
        Assert.Equal(
            "${{ vars.marker }}",
            projected.Definition.Expect!["markers"]!.Value[0].GetProperty("failIf").GetString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    public void Project_PreservesExplicitRecoveryState(int recoveryRemaining)
    {
        var projected = Assert.Single(RuntimeTaskFollowUps.Project([
            new RuntimeTaskInput(
                "review",
                "Review",
                "spec/review",
                Recovery: Recovery,
                RecoveryRemaining: recoveryRemaining)
        ]));

        Assert.Equal(recoveryRemaining, projected.RecoveryRemaining);
    }

    [Fact]
    public void Project_RejectsRecoveryWithoutRemainingState()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RuntimeTaskFollowUps.Project([
            new RuntimeTaskInput("review", "Review", "spec/review", Recovery: Recovery)
        ]));

        Assert.Contains("must carry an explicit numeric recoveryRemaining", error.Message);
    }

    [Fact]
    public void Project_RejectsRemainingStateWithoutRecovery()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RuntimeTaskFollowUps.Project([
            new RuntimeTaskInput("review", "Review", "spec/review", RecoveryRemaining: 1)
        ]));

        Assert.Contains("without a recovery declaration", error.Message);
    }
}
