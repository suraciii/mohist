using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Fails the next <c>WorkflowRuns</c> read issued through the test silo's
/// DbContexts, so a spec can prove what the Server does when one of its owner
/// queries cannot run. Inert until a spec arms it.
/// </summary>
public sealed class WorkflowRunReadFailureProbe
{
    private readonly object _gate = new();
    private bool _armed;
    private string? _failedCommand;

    /// <summary>The statement that was made to fail, or null when none did.</summary>
    public string? FailedCommand
    {
        get
        {
            lock (_gate)
                return _failedCommand;
        }
    }

    public void FailNextWorkflowRunRead()
    {
        lock (_gate)
        {
            _armed = true;
            _failedCommand = null;
        }
    }

    internal void ThrowIfArmed(DbCommand command)
    {
        lock (_gate)
        {
            if (!_armed || !command.CommandText.Contains("WorkflowRuns", StringComparison.Ordinal))
                return;

            _armed = false;
            _failedCommand = command.CommandText;
        }

        throw new InvalidOperationException("simulated WorkflowRuns read failure");
    }
}

public sealed class WorkflowRunReadFailureInterceptor(WorkflowRunReadFailureProbe failures) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        failures.ThrowIfArmed(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        failures.ThrowIfArmed(command);
        return ValueTask.FromResult(result);
    }
}
