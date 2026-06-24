using Scrutor;

namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Extensions that wire Mohist's conventional (assembly-scanned) service
/// registration on top of <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Scans <see cref="MohistServiceRegistration"/>'s assembly for concrete
    /// types implementing <see cref="IScopedService"/> or
    /// <see cref="ISingletonService"/> and registers them as themselves with
    /// the matching lifetime. Types implementing neither marker are
    /// intentionally not registered here.
    /// </summary>
    /// <remarks>
    /// This entry point is called at the very top of
    /// <see cref="MohistServiceRegistration.ConfigureMohistServices"/>, before
    /// any hand-written registration. Microsoft's
    /// <c>IServiceCollection</c> resolves "last registration wins", so any
    /// hand-written registration added afterwards overrides the scanned one
    /// without throwing on duplicate registration.
    /// </remarks>
    public static IServiceCollection AddMohistConventionalServices(this IServiceCollection services)
    {
        var assembly = typeof(MohistServiceRegistration).Assembly;

        services.Scan(scan => scan
            .FromAssemblies(assembly)
            .AddClasses(classes => classes.AssignableTo<IScopedService>())
                .AsSelf()
                .WithScopedLifetime()
            .AddClasses(classes => classes.AssignableTo<ISingletonService>())
                .AsSelf()
                .WithSingletonLifetime());

        return services;
    }
}
