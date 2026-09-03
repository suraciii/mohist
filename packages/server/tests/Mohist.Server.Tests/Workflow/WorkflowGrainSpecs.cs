using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Workflow;

[Collection("WorkflowGrain")]
public abstract class WorkflowGrainSpecs : WorkflowGrainTestContext
{
    protected WorkflowGrainSpecs(WorkflowGrainFixture fixture) : base(fixture) { }
}
