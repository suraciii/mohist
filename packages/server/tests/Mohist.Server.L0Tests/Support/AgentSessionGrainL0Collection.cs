using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L0Tests.Support;

[CollectionDefinition("AgentSessionGrainL0")]
public sealed class AgentSessionGrainL0Collection : ICollectionFixture<AgentSessionGrainFixture>;
