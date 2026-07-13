using Mohist.Server.UnitTests.Agent.Grain;
using Mohist.Server.UnitTests.Workflow;
using Mohist.Server.UnitTests.Workflow.Grain;
using Xunit;

namespace Mohist.Server.UnitTests.Support;

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
