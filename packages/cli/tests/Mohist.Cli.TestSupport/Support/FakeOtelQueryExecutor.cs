namespace Mohist.Cli.TestSupport;

/// <summary>
/// Fake <see cref="IOtelQueryExecutor"/> for otel query specs. Returns a fixed
/// result, or throws a configured <see cref="OtelQueryException"/>, so tests
/// never touch a real SQLite file (design/testing.md hard-constraint 1).
/// </summary>
internal sealed class FakeOtelQueryExecutor : IOtelQueryExecutor
{
    private readonly OtelQueryResult? _result;
    private readonly OtelQueryException? _exception;

    private FakeOtelQueryExecutor(OtelQueryResult? result, OtelQueryException? exception)
    {
        _result = result;
        _exception = exception;
    }

    public static FakeOtelQueryExecutor ReturningColumns(string[] columns, IReadOnlyList<object?[]> rows)
        => new(new OtelQueryResult(columns, rows), exception: null);

    public static FakeOtelQueryExecutor ReturningEmpty()
        => new(new OtelQueryResult(Array.Empty<string>(), Array.Empty<object?[]>()), exception: null);

    public static FakeOtelQueryExecutor Throwing(string message, bool isReadOnlyViolation = false)
        => new(result: null, exception: new OtelQueryException(message, isReadOnlyViolation));

    public Task<OtelQueryResult> ExecuteAsync(string databasePath, string sql, CancellationToken cancellationToken = default)
    {
        if (_exception is not null)
            throw _exception;
        return Task.FromResult(_result!);
    }
}
