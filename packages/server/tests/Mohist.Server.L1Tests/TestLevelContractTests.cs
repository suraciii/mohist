using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L1Tests;

[Trait("level", "L1")]
public sealed class TestLevelContractTests
{
    [Fact]
    public void Every_discovered_test_class_has_exactly_one_direct_L1_trait() =>
        TestLevelContract.AssertAssembly(typeof(TestLevelContractTests).Assembly, "L1");
}
