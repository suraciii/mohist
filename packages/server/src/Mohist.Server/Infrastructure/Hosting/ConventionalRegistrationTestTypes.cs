using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Hosting.TestTypes;

/// <summary>
/// Concrete test type that opts in to conventional Scoped registration via
/// <see cref="IScopedService"/>. Lives in the server assembly so the
/// conventional scanner (<c>AddMohistConventionalServices</c>) discovers it.
/// Public because Scrutor's <c>AddClasses()</c> only picks up public types
/// by default. Kept in a dedicated <c>TestTypes</c> sub-namespace and never
/// referenced by production code.
/// </summary>
public sealed class ConventionalScopedProbe : IScopedService
{
    public int Counter { get; set; }
}

/// <summary>
/// Concrete test type that opts in to conventional Singleton registration via
/// <see cref="ISingletonService"/>. Lives in the server assembly so the
/// conventional scanner (<c>AddMohistConventionalServices</c>) discovers it.
/// Public because Scrutor's <c>AddClasses()</c> only picks up public types
/// by default. Kept in a dedicated <c>TestTypes</c> sub-namespace and never
/// referenced by production code.
/// </summary>
public sealed class ConventionalSingletonProbe : ISingletonService
{
    public int Counter { get; set; }
}

/// <summary>
/// Concrete type that does <em>not</em> implement any conventional marker
/// interface. The scanner MUST NOT register it; resolution from a container
/// that only went through <c>AddMohistConventionalServices</c> must fail.
/// </summary>
public sealed class UnmarkedProbe
{
    public int Counter { get; set; }
}

/// <summary>
/// Concrete type used to verify that a hand-written registration placed
/// after <c>AddMohistConventionalServices</c> wins over the scanned one and
/// that the registration process does not throw on the duplicate descriptor.
/// </summary>
public sealed class ConventionalOverrideProbe : IScopedService
{
    public int Marker { get; init; }
}
