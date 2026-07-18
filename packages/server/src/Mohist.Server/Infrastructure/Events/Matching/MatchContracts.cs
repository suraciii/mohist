namespace Mohist.Server.Infrastructure.Events.Matching;

public sealed record MatchDiagnostic(string Message, int Offset, int Line, int Column);

public sealed class EventMatchCompileResult
{
    private EventMatchCompileResult(EventMatchExpression? expression, MatchDiagnostic? diagnostic)
    {
        Expression = expression;
        Diagnostic = diagnostic;
    }

    public bool IsSuccess => Expression is not null;

    public EventMatchExpression? Expression { get; }

    public MatchDiagnostic? Diagnostic { get; }

    internal static EventMatchCompileResult Success(EventMatchExpression expression) => new(expression, null);

    internal static EventMatchCompileResult Failure(MatchDiagnostic diagnostic) => new(null, diagnostic);
}

public interface IEventMatchFailureSink
{
    void Record(string source, Exception exception);
}

public sealed class NullEventMatchFailureSink : IEventMatchFailureSink
{
    public static NullEventMatchFailureSink Instance { get; } = new();

    private NullEventMatchFailureSink()
    {
    }

    public void Record(string source, Exception exception)
    {
    }
}
