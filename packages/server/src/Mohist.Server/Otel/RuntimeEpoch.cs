namespace Mohist.Server.Otel;

public sealed record RuntimeEpoch
{
    public RuntimeEpoch(DateTimeOffset since)
    {
        Since = since;
    }

    public RuntimeEpoch(TimeProvider timeProvider)
        : this((timeProvider ?? throw new ArgumentNullException(nameof(timeProvider))).GetUtcNow())
    {
    }

    public DateTimeOffset Since { get; }

    public static RuntimeEpoch Capture(TimeProvider timeProvider) => new(timeProvider);

    public static RuntimeEpoch Start(TimeProvider timeProvider) => new(timeProvider);

    public static RuntimeEpoch Create(TimeProvider timeProvider) => new(timeProvider);
}
