using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Support;

[CollectionDefinition("MohistDb")]
public sealed class MohistDbCollection : ICollectionFixture<MohistDbFixture>;
