using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// Best-effort facade over <see cref="IAuthAuditEventStore"/>: the
/// record is awaited (so the product surface can always read it back)
/// but persistence failures only log, because the audited operation
/// already happened and must not be reported as failed on its account.
/// </summary>
public sealed class AuthAuditRecorder : IAuthAuditRecorder, IScopedService
{
    private readonly IAuthAuditEventStore _store;
    private readonly ILogger<AuthAuditRecorder> _logger;

    public AuthAuditRecorder(IAuthAuditEventStore store, ILogger<AuthAuditRecorder> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task RecordAsync(AuthAuditEvent auditEvent, CancellationToken ct = default)
    {
        try
        {
            await _store.RecordAsync(auditEvent, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to record auth audit event {EventType} for subject {SubjectId}",
                auditEvent.EventType,
                auditEvent.SubjectId);
        }
    }
}
