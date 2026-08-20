namespace Mohist.Server.Infrastructure.Hosting;

internal static class OtelPortBindingLog
{
    public static void WriteGenericFailure(Exception ex)
    {
        Console.Error.WriteLine(
            $"[Mohist.Server.Otel] Unexpected failure during host start: {ex}");
    }
}
