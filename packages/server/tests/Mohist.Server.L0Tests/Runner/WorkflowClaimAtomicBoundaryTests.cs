using System.Reflection;
using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.L0Tests.Runner;

[Trait("level", "L0")]
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
        var parameters = method.GetParameters();
        Assert.Equal(4, parameters.Length);
        Assert.Equal("processGeneration", parameters[3].Name);
        Assert.Equal(typeof(string), parameters[3].ParameterType);
        Assert.False(parameters[3].HasDefaultValue);
    }
}
