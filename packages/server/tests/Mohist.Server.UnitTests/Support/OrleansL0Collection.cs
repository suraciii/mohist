using Xunit;

namespace Mohist.Server.UnitTests.Support;

[CollectionDefinition("OrleansGrainL0", DisableParallelization = true)]
public sealed class OrleansL0Collection : ICollectionFixture<OrleansL0WorkflowGrainFixture>;
