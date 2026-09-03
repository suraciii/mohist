using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L0Tests.Support;

[CollectionDefinition("AgentSessionFollowupConcurrency")]
public sealed class AgentSessionFollowupConcurrencyCollection
    : ICollectionFixture<AgentSessionFollowupConcurrencyFixture>;
