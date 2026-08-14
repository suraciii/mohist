using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Services;

[Collection("MohistDb")]
public sealed class WorkflowAgentHandoffPreflightSpecs
{
    private readonly MohistDbFixture _fixture;

    public WorkflowAgentHandoffPreflightSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ResolveAgentAsync_ProductionRegistration_UsesScopedSnapshotResolver()
    {
        var preflight = _fixture.Services.GetRequiredService<IWorkflowAgentHandoffPreflight>();

        var result = await preflight.ResolveAgentAsync(
            $"handoff-preflight-project-{Guid.NewGuid():N}",
            $"agent_missing_{Guid.NewGuid():N}");

        Assert.Null(result);
    }
}
