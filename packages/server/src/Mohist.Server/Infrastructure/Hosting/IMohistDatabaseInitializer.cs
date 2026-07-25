using Mohist.Server.Otel;

namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Applies EF migrations and the repository data upgrade against a
/// host's resolved services before <c>StartAsync</c>. The runner
/// invokes this once for every host attempt (primary and any
/// alternate) using the same concrete dependency, keeping the
/// ordering testable without starting a real <c>WebApplication</c>.
/// </summary>
public interface IMohistDatabaseInitializer
{
    Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IMohistDatabaseInitializer"/> that delegates to
/// the existing <see cref="Mohist.Server.Infrastructure.Data.Db.DatabaseInitializer"/>
/// routine — EF migrations followed by the repository data upgrade.
/// </summary>
public sealed class MohistDatabaseInitializer : IMohistDatabaseInitializer
{
    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        Mohist.Server.Infrastructure.Data.Db.DatabaseInitializer.InitializeAsync(services, cancellationToken);
}
