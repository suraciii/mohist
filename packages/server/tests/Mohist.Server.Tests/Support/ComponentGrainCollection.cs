using Xunit;

namespace Mohist.Server.Tests.Support;

[CollectionDefinition("ComponentGrain", DisableParallelization = true)]
public sealed class ComponentGrainCollection : ICollectionFixture<ComponentWorkflowGrainFixture>;
