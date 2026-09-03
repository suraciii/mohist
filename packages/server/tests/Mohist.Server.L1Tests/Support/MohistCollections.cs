using Mohist.Server.TestSupport;
using Mohist.Server.L1Tests.Specs.Events;
using Mohist.Server.L1Tests.Specs.Agent.Grain;
using Mohist.Server.L1Tests.Specs.Workflow;
using Mohist.Server.L1Tests.Specs.Workflow.Grain;
using Mohist.Server.L1Tests.Specs.Slack;
using Mohist.Server.L1Tests.Specs.GitHub;
using Mohist.Server.L1Tests.Specs.Runner.Api;
using Xunit;

[assembly: AssemblyFixture(typeof(Mohist.Server.L1Tests.Support.DefaultApplicationHostOwner))]

namespace Mohist.Server.L1Tests.Support;

/// <summary>
/// All xUnit <c>[CollectionDefinition]</c> declarations live here. Spec
/// files declare only their membership with
/// <c>[Collection("Name")]</c>; the corresponding definition is here.
/// </summary>
[CollectionDefinition("SlackLeaseRoutes")]
public class SlackLeaseRoutesCollection : ICollectionFixture<SlackAdapterLeaseRoutesFixture>;

[CollectionDefinition("SlackControlPlaneRoutes")]
public class SlackControlPlaneRoutesCollection : ICollectionFixture<SlackControlPlaneRoutesFixture>;

// Slack ingress and interaction specs drive routes that validate the one
// runtime lease per workspace target key, and the lease store is shared by
// every host. Two tests acquiring a lease for the same workspace concurrently
// supersede each other's lease and fail with 409 lease_stale_or_expired.
// These specs also share the process-wide SlackApiTestScript owned by the
// assembly fixture. One collection serializes both resources while unrelated
// collections remain free to use the other spec worker threads.
[CollectionDefinition("SlackApiSurface")]
public class SlackApiSurfaceCollection
    : ICollectionFixture<IsolatedMohistIntegrationFixture>,
      ICollectionFixture<MohistIntegrationFixture>;

// Default consumers request a class adapter so host-free selections leave the
// assembly owner cold without collapsing independently scheduled classes.

[CollectionDefinition("IntegrationRunner")]
public class IntegrationRunnerCollection : ICollectionFixture<DefaultMohistIntegrationFixture>;

[CollectionDefinition("PublicProjectionIntegration")]
public class PublicProjectionIntegrationCollection
    : ICollectionFixture<PublicProjectionIntegrationFixture>;

[CollectionDefinition("RunnerMutationIntegration")]
public class RunnerMutationIntegrationCollection : ICollectionFixture<MohistIntegrationFixture>;

// Runner report arbitration advances durable AgentJob state while a shared
// assembly fixture can advance the same fake clock from unrelated specs.
// Keep this contract on its own host so another collection cannot time out or
// mutate the job between its explicit report steps.
[CollectionDefinition("RunnerPollRecoveryStateApi")]
public class RunnerPollRecoveryStateApiCollection : ICollectionFixture<IsolatedMohistIntegrationFixture>;

[CollectionDefinition("DeviceFlowIntegration")]
public class DeviceFlowIntegrationCollection : ICollectionFixture<IsolatedMohistIntegrationFixture>;

[CollectionDefinition("WorkspaceIntegration")]
public class WorkspaceIntegrationCollection : ICollectionFixture<IsolatedMohistIntegrationFixture>;

// Specs whose contract depends on process- or cluster-wide state share one
// dedicated host per resource domain. Each domain is serial while independent
// domains and ordinary project-scoped specs continue to run in parallel.
[CollectionDefinition("LaunchIntegration")]
public class LaunchIntegrationCollection : ICollectionFixture<IsolatedMohistIntegrationFixture>;

[CollectionDefinition("SessionControlIntegration")]
public class SessionControlIntegrationCollection : ICollectionFixture<IsolatedMohistIntegrationFixture>;

[CollectionDefinition("WorkflowRuntimeIntegration")]
public class WorkflowRuntimeIntegrationCollection : ICollectionFixture<IsolatedMohistIntegrationFixture>;

[CollectionDefinition("RepositoryDataUpgrade")]
public class RepositoryDataUpgradeCollection
    : ICollectionFixture<Specs.Issue.Api.RepositoryDataUpgradeFixture>;

[CollectionDefinition("GitHubCommand")]
public class GitHubCommandCollection : ICollectionFixture<GitHubCommandFixture>;

// OTLP/query route specs share one OtlpRoutesWebApplicationFactory (web host
// + in-memory silo). Tests reset the otel
// tables and collector status via OtlpRoutesHostFixture.ResetOtelStateAsync
// instead of paying a per-test host start.
[CollectionDefinition("IntegrationTelemetry")]
public class IntegrationTelemetryCollection : ICollectionFixture<Specs.Telemetry.OtlpRoutesHostFixture>;

[CollectionDefinition("RunnerConfig")]
public class RunnerConfigCollection : ICollectionFixture<RunnerConfigFixture>;

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

[CollectionDefinition("EventPublishing")]
public class EventPublishingCollection : ICollectionFixture<EventPublishingIntegrationFixture>;

/// <summary>
/// <para>
/// Serializes server-OTel-tracing specs. Every tracing provider subscribes to
/// process-global activity sources, so parallel hosts would pollute each
/// other's assertions.
/// </para>
/// </summary>
[CollectionDefinition("OtelTracing", DisableParallelization = true)]
public class OtelTracingCollection;

/// <summary>
/// Owns the single full Mohist/Orleans host used by OTel integration specs.
/// Lightweight OTel test hosts stay in <c>OtelTracing</c> so this provider is
/// never injected into tests that require their own isolated activity pipeline.
/// </summary>
[CollectionDefinition("OtelFullStackIntegration", DisableParallelization = true)]
public class OtelFullStackIntegrationCollection : ICollectionFixture<OtelIntegrationFixture>;
