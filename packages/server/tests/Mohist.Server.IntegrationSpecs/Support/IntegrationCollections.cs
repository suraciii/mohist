using Mohist.Server.IntegrationSpecs.Specs.Events;
using Mohist.Server.IntegrationSpecs.Specs.Telemetry;
using Xunit;

namespace Mohist.Server.IntegrationSpecs.Support;

[CollectionDefinition("MohistIntegration")]
public class MohistIntegrationCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("IntegrationIssue")]
public class IntegrationIssueCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("IntegrationEpic")]
public class IntegrationEpicCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("IntegrationIssueLifecycle")]
public class IntegrationIssueLifecycleCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("IntegrationIssueRepository")]
public class IntegrationIssueRepositoryCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("IntegrationIssueConfiguration")]
public class IntegrationIssueConfigurationCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("IntegrationApi")]
public class IntegrationApiCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("IntegrationSessions")]
public class IntegrationSessionsCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("IntegrationWorkflow")]
public class IntegrationWorkflowCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("IntegrationRunner")]
public class IntegrationRunnerCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("IntegrationMisc")]
public class IntegrationMiscCollection : ICollectionFixture<MohistIntegrationFixture>;

[CollectionDefinition("IntegrationTelemetry")]
public class IntegrationTelemetryCollection : ICollectionFixture<OtlpRoutesHostFixture>;

[CollectionDefinition("EventPublishing")]
public class EventPublishingCollection : ICollectionFixture<EventPublishingIntegrationFixture>;

[CollectionDefinition("OtelTracing", DisableParallelization = true)]
public class OtelTracingCollection;
