using Orleans;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    private static readonly TimeSpan PersistTimerPeriod = TimeSpan.FromMilliseconds(200);
    private readonly TimeSpan _persistTimerDueTime;

    private void EnsurePersistenceTimer()
    {
        _persistTimer ??= this.RegisterGrainTimer(
            _ => PersistCallback(),
            _persistTimerDueTime,
            PersistTimerPeriod);
    }
}
