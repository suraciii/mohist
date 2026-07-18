namespace Mohist.Server.Infrastructure.Events.Matching;

public sealed class EventMatchExpression
{
    public static TimeSpan DefaultRegexTimeout { get; } = TimeSpan.FromMilliseconds(100);

    private readonly IBooleanMatchNode _root;
    private readonly IEventMatchFailureSink _failureSink;

    private EventMatchExpression(string source, IBooleanMatchNode root, IEventMatchFailureSink failureSink)
    {
        Source = source;
        _root = root;
        _failureSink = failureSink;
    }

    public string Source { get; }

    public static EventMatchCompileResult Compile(
        string source,
        TimeSpan? regexTimeout = null,
        IEventMatchFailureSink? failureSink = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var timeout = regexTimeout ?? DefaultRegexTimeout;
        if (timeout <= TimeSpan.Zero && timeout != System.Text.RegularExpressions.Regex.InfiniteMatchTimeout)
        {
            return EventMatchCompileResult.Failure(
                new MatchDiagnostic("Regex timeout must be positive or infinite.", 0, 1, 1));
        }

        try
        {
            var tokens = new MatchTokenizer(source).Tokenize();
            var root = new MatchParser(tokens, timeout).Parse();
            return EventMatchCompileResult.Success(
                new EventMatchExpression(source, root, failureSink ?? NullEventMatchFailureSink.Instance));
        }
        catch (MatchParseException exception)
        {
            return EventMatchCompileResult.Failure(exception.Diagnostic);
        }
    }

    public bool Matches(EventMatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            return _root.Evaluate(input);
        }
        catch (Exception exception)
        {
            try
            {
                _failureSink.Record(Source, exception);
            }
            catch (Exception)
            {
            }
            return false;
        }
    }
}
