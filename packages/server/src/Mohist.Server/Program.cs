using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;

var epoch = RuntimeEpoch.Capture(TimeProvider.System);
var factory = new MohistHostFactory(args, WebApplication.CreateBuilder(args));
var primaryPlan = factory.CreatePrimaryPlan(epoch);
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var classifier = new OtelBindFailureClassifier(loggerFactory.CreateLogger<OtelBindFailureClassifier>());
var initializer = new MohistDatabaseInitializer();
var runnerLogger = loggerFactory.CreateLogger<MohistHostRunner>();
var runner = new MohistHostRunner(factory, classifier, initializer, runnerLogger);

try
{
    await runner.RunAsync(primaryPlan, CancellationToken.None);
}
catch (Exception ex)
{
    OtelPortBindingLog.WriteGenericFailure(ex);
    throw;
}

public partial class Program { }

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
