using System.Reflection;
using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Runner;

public sealed class WorkflowClaimAtomicBoundaryTests
{
    [Fact]
    public void WorkflowClaimDoesNotAdvertiseAnIgnoredCapabilityExpectation()
    {
        var method = typeof(IRunnerGrain).GetMethod(nameof(IRunnerGrain.TryClaimWorkflowAsync));

        Assert.NotNull(method);
        Assert.DoesNotContain(
            method!.GetParameters(),
            parameter => parameter.ParameterType == typeof(CapabilityClaimExpectation));
        Assert.Equal(3, method.GetParameters().Length);
    }
}
