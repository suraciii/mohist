namespace Mohist.Server.Infrastructure.Hosting;

internal static class OtelPortBindingLog
{
    public static void WriteBindFailure(int port, string host, Exception ex)
    {
        Console.Error.WriteLine(
            $"[Mohist.Server.Otel] Failed to bind OTLP ingestion port {port} on {host}; " +
            $"collector will report offline. Main API continues normally. {ex.Message}");
    }

    public static void WriteGenericFailure(Exception ex)
    {
        Console.Error.WriteLine(
            $"[Mohist.Server.Otel] Unexpected failure during host start: {ex}");
    }
}
