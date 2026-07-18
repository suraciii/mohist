using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grains;

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
