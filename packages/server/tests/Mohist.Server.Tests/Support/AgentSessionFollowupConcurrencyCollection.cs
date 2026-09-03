using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Support;

[CollectionDefinition("AgentSessionFollowupConcurrency")]
public sealed class AgentSessionFollowupConcurrencyCollection
    : ICollectionFixture<AgentSessionFollowupConcurrencyFixture>;
