using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace Mohist.Server.Tests.Platform.Otel;

/// <summary>
/// Pins the production tracing registration's Entity Framework source
/// subscription without standing up the application host or a collector.
/// </summary>
[Trait("level", "L0")]
public sealed class OtelSourceSubscriptionSpecs
{
    [Fact]
    public async Task ConfigureTracing_RegistersEntityFrameworkCoreInstrumentation()
    {
        var services = new ServiceCollection();
        var recorded = new List<Activity>();
        var builder = services.AddOpenTelemetry();
        MohistOpenTelemetryRegistration.ConfigureTelemetry(
            builder,
            new OtelOptions
            {
                Enabled = true,
                ExportEnabled = false,
                Endpoint = "http://collector.test/otel",
            });
        builder.WithTracing(tracing => tracing.AddProcessor(new RecordingActivityProcessor(recorded)));

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();

        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new MohistDbContext(options);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE Probe (Id INTEGER PRIMARY KEY);");

        Assert.Contains(
            recorded,
            activity => activity.Source?.Name == "OpenTelemetry.Instrumentation.EntityFrameworkCore");
    }

    private sealed class RecordingActivityProcessor(ICollection<Activity> ended) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity) => ended.Add(activity);
    }
}
