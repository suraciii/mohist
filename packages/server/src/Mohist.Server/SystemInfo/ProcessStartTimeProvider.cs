using System.Diagnostics;

namespace Mohist.Server.SystemInfo;

/// <summary>
/// Default <see cref="IProcessStartTimeProvider"/> implementation. This
/// is the only place a real process-info read is allowed for the
/// stale-job threshold; the reconciler itself never touches process
/// info or wall-clock APIs directly.
/// </summary>
public sealed class ProcessStartTimeProvider : IProcessStartTimeProvider
{
    private readonly Func<Process> _processAccessor;

    public ProcessStartTimeProvider()
        : this(Process.GetCurrentProcess)
    {
    }

    internal ProcessStartTimeProvider(Func<Process> processAccessor)
    {
        _processAccessor = processAccessor;
    }

    public DateTimeOffset GetStartTime()
    {
        using var process = _processAccessor();
        return process.StartTime.ToUniversalTime();
    }
}