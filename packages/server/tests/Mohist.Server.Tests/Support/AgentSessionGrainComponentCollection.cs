using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Support;

[CollectionDefinition("AgentSessionGrainComponent")]
public sealed class AgentSessionGrainComponentCollection : ICollectionFixture<AgentSessionGrainFixture>;
