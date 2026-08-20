using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;

var epoch = RuntimeEpoch.Capture(TimeProvider.System);
var builder = WebApplication.CreateBuilder(args);
var factory = new MohistHostFactory(args, builder);
var primaryPlan = factory.CreatePrimaryPlan(epoch);
using var loggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
    logging.AddConsole();
});
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
