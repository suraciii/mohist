namespace Mohist.Server.Infrastructure.Errors;

/// <summary>
/// Base class for domain-level errors that the API should map to
/// structured HTTP responses. Subclasses are caught by
/// <c>ExceptionMiddleware</c> and translated to a status code:
/// <list type="bullet">
///   <item><see cref="DomainNotFoundException"/> → 404</item>
///   <item><see cref="DomainConflictException"/> → 409</item>
///   <item><see cref="DomainValidationException"/> → 400</item>
/// </list>
/// Use these in place of bare <c>InvalidOperationException</c> so the
/// API layer does not have to guess intent from the message string.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message) { }
    protected DomainException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>
/// A referenced row (issue, project, workflow, etc.) does not exist.
/// Maps to HTTP 404. Carries an optional <see cref="Resource"/> and
/// <see cref="ResourceId"/> so the response can be machine-readable.
/// </summary>
public sealed class DomainNotFoundException : DomainException
{
    public string Resource { get; }
    public string? ResourceId { get; }

    public DomainNotFoundException(string resource, string? resourceId, string? message = null)
        : base(message ?? $"{resource} '{resourceId}' not found")
    {
        Resource = resource;
        ResourceId = resourceId;
    }
}

/// <summary>
/// The operation is not legal in the current state of the resource
/// (e.g. closing a Done issue, completing a Backlog issue, starting
/// a workflow on a project without prerequisites). Maps to HTTP 409.
/// </summary>
public sealed class DomainConflictException : DomainException
{
    public DomainConflictException(string message) : base(message) { }
    public DomainConflictException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>
/// Caller-supplied input is structurally invalid for the operation
/// (e.g. an Issue prerequisite referencing itself, a Project missing
/// a required path or remote). Maps to HTTP 400.
/// </summary>
public sealed class DomainValidationException : DomainException
{
    public DomainValidationException(string message) : base(message) { }
    public DomainValidationException(string message, Exception? inner) : base(message, inner) { }
}
