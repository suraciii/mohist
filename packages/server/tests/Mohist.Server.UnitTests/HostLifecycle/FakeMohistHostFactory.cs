using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.UnitTests.HostLifecycle;

/// <summary>
/// Records initializer/stop/dispose calls so runner tests can verify
/// the exact sequence. Each host instance is captured and timed.
/// </summary>
public sealed class FakeMohistHostFactory : IMohistHostFactory
{
    private readonly List<FakeMohistHost> _primaryHosts = new();
    private readonly List<FakeMohistHost> _alternateHosts = new();
    private readonly Func<int, FakeMohistHost>? _primaryFactory;
    private readonly Func<int, FakeMohistHost>? _alternateFactory;

    public FakeMohistHostFactory(
        Func<int, FakeMohistHost>? primaryFactory = null,
        Func<int, FakeMohistHost>? alternateFactory = null)
    {
        _primaryFactory = primaryFactory;
        _alternateFactory = alternateFactory;
    }

    public IReadOnlyList<FakeMohistHost> PrimaryHosts => _primaryHosts;
    public IReadOnlyList<FakeMohistHost> AlternateHosts => _alternateHosts;

    public IMohistHost CreatePrimary(MohistHostPlan plan)
    {
        var index = _primaryHosts.Count;
        var host = _primaryFactory?.Invoke(index) ?? new FakeMohistHost($"primary-{index}");
        _primaryHosts.Add(host);
        return host;
    }

    public IMohistHost CreateAlternate(MohistHostPlan plan)
    {
        var index = _alternateHosts.Count;
        var host = _alternateFactory?.Invoke(index) ?? new FakeMohistHost($"alternate-{index}");
        _alternateHosts.Add(host);
        return host;
    }
}
