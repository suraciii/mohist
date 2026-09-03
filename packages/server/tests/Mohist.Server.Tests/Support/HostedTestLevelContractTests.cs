using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests;

[Trait("level", "L1")]
public sealed class HostedTestLevelContractTests
{
    [Fact]
    public void Every_discovered_test_class_has_exactly_one_direct_level_trait() =>
        TestLevelContract.AssertAssembly(typeof(HostedTestLevelContractTests).Assembly);
}
