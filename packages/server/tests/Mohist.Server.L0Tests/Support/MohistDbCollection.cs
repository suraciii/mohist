using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L0Tests.Support;

[CollectionDefinition("MohistDb")]
public sealed class MohistDbCollection : ICollectionFixture<MohistDbFixture>;
