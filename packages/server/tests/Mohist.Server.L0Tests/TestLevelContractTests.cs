using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L0Tests;

[Trait("level", "L0")]
public sealed class TestLevelContractTests
{
    [Fact]
    public void Every_discovered_test_class_has_exactly_one_direct_L0_trait() =>
        TestLevelContract.AssertAssembly(typeof(TestLevelContractTests).Assembly, "L0");
}
