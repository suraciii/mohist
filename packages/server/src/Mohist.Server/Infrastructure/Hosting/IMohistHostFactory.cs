namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Constructs the primary and alternate host attempts through one
/// shared build path, so registrations, routes, and the single sampler
/// registration are identical between attempts and only differ by the
/// listener intent and initial collector result carried in the
/// <see cref="MohistHostPlan"/>.
/// </summary>
public interface IMohistHostFactory
{
    IMohistHost CreatePrimary(MohistHostPlan plan);

    IMohistHost CreateAlternate(MohistHostPlan plan);
}
