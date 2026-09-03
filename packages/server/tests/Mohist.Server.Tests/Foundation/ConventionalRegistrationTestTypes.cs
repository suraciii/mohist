using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Tests.Foundation;

public sealed class ConventionalScopedProbe : IScopedService
{
    public int Counter { get; set; }
}

public sealed class ConventionalSingletonProbe : ISingletonService
{
    public int Counter { get; set; }
}

public sealed class UnmarkedProbe
{
    public int Counter { get; set; }
}

public sealed class ConventionalOverrideProbe : IScopedService
{
    public int Marker { get; init; }
}
