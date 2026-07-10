using Mohist.Server.ComponentSpecs.Specs.Agent.Grain;
using Mohist.Server.ComponentSpecs.Specs.Workflow;
using Mohist.Server.ComponentSpecs.Specs.Workflow.Grain;
using Xunit;

namespace Mohist.Server.ComponentSpecs.Support;

[CollectionDefinition("MohistDb")]
public class MohistDbCollection : ICollectionFixture<MohistDbFixture>;

// Grain-fixture collections host an InProcessTestCluster, which uses
// in-memory transport (InProcessMembershipTable / InMemoryTransport) —
// no TCP ports — so they can run in parallel with everything else.

[CollectionDefinition("WorkflowGrain")]
public class WorkflowGrainCollection : ICollectionFixture<WorkflowGrainFixture>;

[CollectionDefinition("RunnerGrain")]
public class RunnerGrainCollection : ICollectionFixture<WorkflowGrainFixture>;

[CollectionDefinition("AgentJobGrain")]
public class AgentJobGrainCollection : ICollectionFixture<AgentJobGrainFixture>;

[CollectionDefinition("Backlog")]
public class BacklogCollection : ICollectionFixture<BacklogFixture>;

[CollectionDefinition("OtelTracing", DisableParallelization = true)]
public class OtelTracingCollection;
