using Mohist.Server.Tests.Specs.Events;
using Mohist.Server.Tests.Specs.Workflow;
using Mohist.Server.Tests.Specs.Workflow.Grain;
using Xunit;

namespace Mohist.Server.Tests.Support;

/// <summary>
/// All xUnit <c>[CollectionDefinition]</c> declarations live here. Spec
/// files declare only their membership with
/// <c>[Collection("Name")]</c>; the corresponding definition is here.
/// </summary>
[CollectionDefinition("MohistIntegration")]
public class MohistIntegrationCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("MohistDb")]
public class MohistDbCollection : ICollectionFixture<MohistDbFixture>;

[CollectionDefinition("WorkflowGrain", DisableParallelization = true)]
public class WorkflowGrainCollection : ICollectionFixture<WorkflowGrainFixture>;

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
