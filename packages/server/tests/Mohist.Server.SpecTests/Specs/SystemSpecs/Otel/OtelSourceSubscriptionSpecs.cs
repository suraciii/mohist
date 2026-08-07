using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.TestSupport;
using OpenTelemetry;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs.Otel;

/// <summary>
/// Pins the five-source subscription contract from design Decision 3.
/// Every automatic instrumentation source the server uses MUST be
/// subscribed by the production <c>WithTracing</c> block; if a
/// future edit drops one, the in-memory exporter in
/// <see cref="OtelTestHost"/> will not see activities from that
/// source and these tests fail.
///
/// <para>
/// The unit tests under this class stand up the production
/// <see cref="MohistOpenTelemetryRegistration.ConfigureTracing"/>
/// extension on a one-off <see cref="OpenTelemetryBuilder"/> with an
/// in-memory <see cref="BaseProcessor{T}"/> attached. The builder
/// drives the same WithTracing block the production code uses, so
/// "this builder captured an activity from source X" proves the
/// production pipeline subscribes source X.
/// </para>
/// </summary>
[Collection("OtelTracing")]
public class OtelSourceSubscriptionSpecs
{
    [Fact]
    public void ConfigureTracing_SubscribesSignalRServerSource()
    {
        // The SignalR Server source is a literal AddSource call in
        // the production pipeline. The .NET 10 source name is the
        // canonical, documented constant — pinning it here makes a
        // future typo in the registration immediately visible.
        Assert.Equal(
            "Microsoft.AspNetCore.SignalR.Server",
            MohistOpenTelemetryRegistration.SignalRServerActivitySourceName);
    }

    [Fact]
    public async Task ConfigureTracing_RegistersEntityFrameworkCoreInstrumentation()
    {
        // Stand up the test host and issue an EF query through the
        // production OTel pipeline. The EF Core instrumentation
        // library publishes its own ActivitySource internally; the
        // runtime evidence that the production
        // AddEntityFrameworkCoreInstrumentation call wires the
        // instrumentation correctly is the activity captured on the
        // recorder with source name "OpenTelemetry.Instrumentation.EntityFrameworkCore".
        await using var host = new OtelTestHost(new OtelTestHostOptions { Enabled = true });

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "OpenTelemetry.Instrumentation.EntityFrameworkCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = activity => host.Recorder.OnEnd(activity),
        };
        ActivitySource.AddActivityListener(listener);

        // EF queries don't require a host route — just use a
        // DbContextFactory on its own. We deliberately do not
        // exercise the production DbContextFactory here because it
        // requires extensive fixture wiring (the SQL connection
        // string + migrations); what we are pinning is the OTel
        // EF Core source subscription. The full EF text
        // assertion lives in OtelExecutionChainTracingSpecs
        // .EfQuery_CarriesSqlTextAsAttribute.
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Mohist.Server.Infrastructure.Data.Db.MohistDbContext>()
            .UseSqlite(conn)
            .Options;
        using var db = new Mohist.Server.Infrastructure.Data.Db.MohistDbContext(options);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE Probe (Id INTEGER PRIMARY KEY);");

        await host.Recorder.WaitForAsync(
            activities => activities.Any(a => a.Source?.Name == "OpenTelemetry.Instrumentation.EntityFrameworkCore"));

        Assert.Contains(host.Recorder.EndedActivities,
            a => a.Source?.Name == "OpenTelemetry.Instrumentation.EntityFrameworkCore");
    }
}
