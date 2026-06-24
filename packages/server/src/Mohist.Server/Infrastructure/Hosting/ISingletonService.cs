namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Marker interface for concrete services that should be auto-registered
/// with a <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton"/>
/// lifetime by <see cref="ServiceCollectionExtensions.AddMohistConventionalServices"/>.
/// Implementations are registered as themselves (<c>AsSelf()</c>) into the
/// single server assembly scanned by the conventional registration entry point.
/// </summary>
public interface ISingletonService;
