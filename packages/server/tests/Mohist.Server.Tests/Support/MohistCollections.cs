using Mohist.Server.Tests.Specs.Events;
using Mohist.Server.Tests.Specs.Agent.Grain;
using Mohist.Server.Tests.Specs.Workflow;
using Mohist.Server.Tests.Specs.Workflow.Grain;
using Xunit;

namespace Mohist.Server.Tests.Support;

/// <summary>
/// All xUnit <c>[CollectionDefinition]</c> declarations live here. Spec
/// files declare only their membership with
/// <c>[Collection("Name")]</c>; the corresponding definition is here.
/// </summary>
[CollectionDefinition("MohistIntegration", DisableParallelization = true)]
public class MohistIntegrationCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("MohistDb")]
public class MohistDbCollection : ICollectionFixture<MohistDbFixture>;

[CollectionDefinition("WorkflowGrain", DisableParallelization = true)]
public class WorkflowGrainCollection : ICollectionFixture<WorkflowGrainFixture>;

[CollectionDefinition("AgentJobGrain", DisableParallelization = true)]
public class AgentJobGrainCollection : ICollectionFixture<AgentJobGrainFixture>;

[CollectionDefinition("Backlog", DisableParallelization = true)]
public class BacklogCollection : ICollectionFixture<BacklogFixture>;

[CollectionDefinition("WorkflowEvents", DisableParallelization = true)]
public class WorkflowEventsCollection;

[CollectionDefinition("SkillsCli", DisableParallelization = true)]
public sealed class SkillsCliCollection;

[CollectionDefinition("WorkflowCli", DisableParallelization = true)]
public sealed class WorkflowCliCollection;

[CollectionDefinition("EventPublishing", DisableParallelization = true)]
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
