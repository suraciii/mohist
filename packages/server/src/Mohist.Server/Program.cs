using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;

var epoch = RuntimeEpoch.Capture(TimeProvider.System);

var otelSection = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

var otelListenerIntent = ReadListenerIntent(otelSection);
var otelEnabled = ReadOtelEnabled(otelSection);

var primaryPlan = MohistHostPlan.Primary(epoch, otelEnabled, otelListenerIntent);

var factory = new MohistHostFactory(args);
var classifier = new OtelBindFailureClassifier();
var initializer = new MohistDatabaseInitializer();
var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
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

static OtelListenerIntent? ReadListenerIntent(IConfiguration configuration)
{
    var enabled = ReadOtelEnabled(configuration);
    if (!enabled)
        return null;

    var bindHost = configuration["Mohist:Otel:BindHost"];
    if (string.IsNullOrWhiteSpace(bindHost))
        bindHost = "localhost";

    var portValue = configuration["Mohist:Otel:Port"];
    var port = int.TryParse(portValue, out var parsed) ? parsed : OtelOptions.DefaultPort;
    return new OtelListenerIntent(bindHost, port);
}

static bool ReadOtelEnabled(IConfiguration configuration)
{
    var value = configuration["Mohist:Otel:Enabled"];
    return bool.TryParse(value, out var enabled) && enabled;
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
