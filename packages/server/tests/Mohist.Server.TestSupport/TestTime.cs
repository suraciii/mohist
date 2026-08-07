namespace Mohist.Server.TestSupport;

internal static class TestTime
{
    public static readonly DateTimeOffset UtcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static DateTime UtcDateTime => UtcNow.UtcDateTime;
}
