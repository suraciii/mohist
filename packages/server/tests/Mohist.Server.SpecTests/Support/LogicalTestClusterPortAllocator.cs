using System.Reflection;
using Orleans.TestingHost;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Supplies endpoint identities for Orleans' in-process transport without
/// probing or binding host sockets. Orleans' <see cref="InProcessTestClusterBuilder"/>
/// still invokes its default port allocator before it installs the in-memory
/// transport, so the default allocator is not hermetic in restricted runners.
/// </summary>
internal sealed class LogicalTestClusterPortAllocator : ITestClusterPortAllocator
{
    private static int _nextPort = 10_000;

    public ValueTuple<int, int> AllocateConsecutivePortPairs(int numPorts)
    {
        if (numPorts <= 0)
            throw new ArgumentOutOfRangeException(nameof(numPorts));

        var width = checked(numPorts + 1);
        var siloPort = Interlocked.Add(ref _nextPort, width) - width;
        var gatewayPort = Interlocked.Add(ref _nextPort, width) - width;
        return (siloPort, gatewayPort);
    }

    public void Dispose()
    {
    }
}

internal static class InProcessTestClusterBuilderExtensions
{
    private static readonly FieldInfo PortAllocatorField =
        typeof(InProcessTestClusterBuilder).GetField(
            "<PortAllocator>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The Orleans in-process cluster builder no longer exposes its port allocator backing field.");

    public static InProcessTestClusterBuilder UseLogicalPorts(this InProcessTestClusterBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        PortAllocatorField.SetValue(builder, new LogicalTestClusterPortAllocator());
        return builder;
    }
}
