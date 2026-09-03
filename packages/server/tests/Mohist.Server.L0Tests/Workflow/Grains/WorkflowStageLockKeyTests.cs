using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.Grains;

[Trait("level", "L0")]
public sealed class WorkflowStageLockKeyTests
{
    [Fact]
    public void RepositoryScopedKey_IsDelimiterSafeAndSeparatesRepositoryTuples()
    {
        var first = WorkflowStageLockKeys.ForProjectRepositoryResource("project:one", "server|blue", "project-integration");
        var second = WorkflowStageLockKeys.ForProjectRepositoryResource("project:one", "server", "blue|project-integration");

        Assert.NotEqual(first, second);
        Assert.Contains("server|blue", first);
        Assert.Contains("project-integration", first);
    }
}
