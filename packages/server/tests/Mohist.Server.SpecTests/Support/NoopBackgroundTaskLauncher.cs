using Mohist.Server.Infrastructure;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// No-op <see cref="IBackgroundTaskLauncher"/> for unit-style specs that
/// exercise a single store in isolation. Every launched work item is
/// recorded into <see cref="Launched"/> so a spec can still assert that the
/// store scheduled the expected follow-up without depending on a real
/// background scheduler.
///
/// Spec fixtures that go through <see cref="GrainTestConfig.ConfigureSilo"/>
/// keep using the real <see cref="BackgroundTaskLauncher"/>; this fake
/// exists for direct construction of <see cref="Mohist.Server.Infrastructure.Data.Workflow.WorkflowRunStore"/>,
/// <see cref="Mohist.Server.Infrastructure.Data.Issue.IssueStore"/>, and
/// <see cref="Mohist.Server.Infrastructure.Data.Sessions.AgentSessionStore"/>
/// in unit-style tests where the production launcher would otherwise leak
/// fire-and-forget tasks across tests.
/// </summary>
public sealed class NoopBackgroundTaskLauncher : IBackgroundTaskLauncher
{
    public static NoopBackgroundTaskLauncher Instance { get; } = new();

    private readonly object _gate = new();
    private readonly List<LaunchRecord> _launched = [];

    private NoopBackgroundTaskLauncher()
    {
    }

    public IReadOnlyList<LaunchRecord> Launched
    {
        get
        {
            lock (_gate)
            {
                return _launched.ToList();
            }
        }
    }

    public void Launch(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            _launched.Add(new LaunchRecord(cancellationToken));
        }
    }

    public sealed record LaunchRecord(CancellationToken CancellationToken);
}
