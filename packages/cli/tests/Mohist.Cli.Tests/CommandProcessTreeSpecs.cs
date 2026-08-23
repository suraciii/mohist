using Xunit;

namespace Mohist.Cli.Tests;

public class CommandProcessTreeSpecs
{
    [Fact]
    public void FindDescendantIds_ReturnsOnlyTheTransitiveTree()
    {
        var descendants = CommandProcessTree.FindDescendantIds(
            10,
            [
                (11, 10),
                (12, 11),
                (13, 12),
                (20, 99),
            ]);

        Assert.Equal([11, 12, 13], descendants.Order());
    }
}
