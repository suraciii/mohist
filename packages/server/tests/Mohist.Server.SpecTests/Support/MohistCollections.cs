using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Events;
using Mohist.Server.SpecTests.Specs.Agent.Grain;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.SpecTests.Specs.Workflow.Grain;
using Mohist.Server.SpecTests.Specs.Slack;
using Mohist.Server.SpecTests.Specs.GitHub;
using Xunit;

[assembly: AssemblyFixture(typeof(Mohist.Server.SpecTests.Support.MohistIntegrationFixture))]

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// All xUnit <c>[CollectionDefinition]</c> declarations live here. Spec
/// files declare only their membership with
/// <c>[Collection("Name")]</c>; the corresponding definition is here.
/// </summary>
[CollectionDefinition("SlackLeaseRoutes")]
public class SlackLeaseRoutesCollection : ICollectionFixture<SlackAdapterLeaseRoutesFixture>;

[CollectionDefinition("SlackControlPlaneRoutes")]
public class SlackControlPlaneRoutesCollection : ICollectionFixture<SlackControlPlaneRoutesFixture>;

// Full-stack HTTP/Orleans specs receive the assembly fixture directly. Their
// default per-class collections remain available for parallel scheduling.

[CollectionDefinition("PublicProjectionIntegration")]
public class PublicProjectionIntegrationCollection
    : ICollectionFixture<PublicProjectionIntegrationFixture>;

[CollectionDefinition("RunnerMutationIntegration")]
public class RunnerMutationIntegrationCollection : ICollectionFixture<MohistIntegrationFixture>;

// Specs whose contract depends on process- or cluster-wide state share one
// dedicated host. The collection is serial; ordinary project-scoped specs
// continue to run in parallel on the assembly fixture.
[CollectionDefinition("IsolatedIntegration")]
public class IsolatedIntegrationCollection : ICollectionFixture<IsolatedMohistIntegrationFixture>;

[CollectionDefinition("RepositoryDataUpgrade")]
public class RepositoryDataUpgradeCollection
    : ICollectionFixture<Specs.Issue.Api.RepositoryDataUpgradeFixture>;

[CollectionDefinition("AgentStatusHistoryBounded")]
public class AgentStatusHistoryBoundedCollection
    : ICollectionFixture<Specs.Sessions.AgentStatusHistoryBoundedFixture>;

[CollectionDefinition("GitHubFeed")]
public class GitHubFeedCollection : ICollectionFixture<GitHubFeedFixture>;

// OTLP/query route specs share one OtlpRoutesWebApplicationFactory (web host
// + in-memory silo). Tests reset the otel
// tables and collector status via OtlpRoutesHostFixture.ResetOtelStateAsync
// instead of paying a per-test host start.
[CollectionDefinition("IntegrationTelemetry")]
public class IntegrationTelemetryCollection : ICollectionFixture<Specs.Telemetry.OtlpRoutesHostFixture>;

[CollectionDefinition("MohistDb")]
public class MohistDbCollection : ICollectionFixture<MohistDbFixture>;

// Grain-fixture collections host an InProcessTestCluster, which uses
// in-memory transport (InProcessMembershipTable / InMemoryTransport) —
// no TCP ports — so they can run in parallel with everything else.

[CollectionDefinition("WorkflowGrain")]
public class WorkflowGrainCollection : ICollectionFixture<WorkflowGrainFixture>;

[CollectionDefinition("WorkflowExecution")]
public class WorkflowExecutionCollection : ICollectionFixture<WorkflowGrainFixture>;

[CollectionDefinition("WorkflowRecovery")]
public class WorkflowRecoveryCollection : ICollectionFixture<WorkflowGrainFixture>;

[CollectionDefinition("RunnerGrain")]
public class RunnerGrainCollection : ICollectionFixture<WorkflowGrainFixture>;

[CollectionDefinition("AgentJobGrain")]
public class AgentJobGrainCollection : ICollectionFixture<AgentJobGrainFixture>;

[CollectionDefinition("AgentSpawnCoordinator", DisableParallelization = true)]
public class AgentSpawnCoordinatorCollection : ICollectionFixture<AgentJobGrainFixture>;

[CollectionDefinition("Backlog")]
public class BacklogCollection : ICollectionFixture<BacklogFixture>;

[CollectionDefinition("EventPublishing")]
public class EventPublishingCollection : ICollectionFixture<EventPublishingIntegrationFixture>;

/// <summary>
/// <para>
/// Serializes the server-OTel-tracing specs. They each stand up a
/// <see cref="WebApplication"/> with its own OTel
/// <c>TracerProvider</c>, and every provider subscribes to the same
/// process-global <c>Microsoft.AspNetCore</c> ActivitySource. When two
/// OTel tests run in parallel, the inbound-HTTP activities of one host
/// flow through the other host's recorder and pollute its assertions —
/// a passing test "no spans under /otel/" can suddenly see spans from
/// the other test's <c>/api/health</c> request. Running these tests in
/// a single non-parallel collection guarantees each
/// <see cref="OtelTestHost"/>'s recorder sees only its own requests.
/// </para>
/// <para>
/// No shared fixture is needed (each test creates its own host), so
/// <see cref="ICollectionFixture{TFixture}"/> is intentionally not
/// applied.
/// </para>
/// </summary>
[CollectionDefinition("OtelTracing", DisableParallelization = true)]
public class OtelTracingCollection;
