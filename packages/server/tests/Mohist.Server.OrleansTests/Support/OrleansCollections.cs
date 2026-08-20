using Mohist.Server.SpecTests.Specs.Workflow;
using Xunit;

namespace Mohist.Server.OrleansTests.Support;

[CollectionDefinition("WorkflowGrain")]
public sealed class WorkflowGrainCollection : ICollectionFixture<WorkflowGrainFixture>;

[CollectionDefinition("RunnerGrain")]
public sealed class RunnerGrainCollection : ICollectionFixture<WorkflowGrainFixture>;
