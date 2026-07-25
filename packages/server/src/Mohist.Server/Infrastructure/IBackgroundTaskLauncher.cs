using Mohist.Server.Otel;

namespace Mohist.Server.Infrastructure;

public interface IBackgroundTaskLauncher
{
    void Launch(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default);
}

public sealed class BackgroundTaskLauncher : IBackgroundTaskLauncher
{
    public void Launch(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        using var ambient = RequestWorkScope.Push(null);
        _ = Task.Run(() => work(cancellationToken), cancellationToken);
    }
}
