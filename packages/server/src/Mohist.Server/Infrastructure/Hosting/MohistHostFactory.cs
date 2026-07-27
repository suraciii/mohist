using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Logging;
using Mohist.Server.Otel;

namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Production factory that builds each host attempt through one
/// composition path. The plan drives both the listener registration and
/// the initial <see cref="CollectorResult"/> carried into the host's
/// <see cref="RuntimeObservability"/>; everything else (DI, Orleans,
/// routes and middleware registration) is shared.
/// </summary>
/// <remarks>
/// <para>
/// One production <see cref="WebApplicationBuilder"/> composes the
/// full graph for both primary and alternate hosts. The factory
/// exposes <see cref="ApplyPlan"/> so a non-starting composition
/// test can inspect the resulting service descriptors before any
/// host construction occurs.
/// </para>
/// </remarks>
public sealed class MohistHostFactory : IMohistHostFactory
{
    private readonly string[] _args;
    private WebApplicationBuilder? _primaryBuilder;

    public MohistHostFactory(string[] args, WebApplicationBuilder primaryBuilder)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(primaryBuilder);
        _args = args;
        _primaryBuilder = PrepareBuilder(primaryBuilder);
    }

    public MohistHostPlan CreatePrimaryPlan(RuntimeEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        var configuration = (_primaryBuilder ?? throw new InvalidOperationException("Primary builder has already been consumed.")).Configuration;
        var otelOptions = configuration
            .GetSection(Mohist.Server.Otel.OtelOptions.SectionName)
            .Get<Mohist.Server.Otel.OtelOptions>()
            ?? new Mohist.Server.Otel.OtelOptions();
        var enabled = otelOptions.Enabled;
        var listenerIntent = enabled
            ? new OtelListenerIntent(otelOptions.BindHost, otelOptions.Port)
            : null;
        return MohistHostPlan.Primary(epoch, enabled, listenerIntent);
    }

    public IMohistHost CreatePrimary(MohistHostPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var builder = Interlocked.Exchange(ref _primaryBuilder, null)
            ?? throw new InvalidOperationException("Primary host has already been created.");
        return Build(plan, builder);
    }

    public IMohistHost CreateAlternate(MohistHostPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Build(plan, PrepareBuilder(WebApplication.CreateBuilder(_args)));
    }

    /// <summary>
    /// One shared composition path: Kestrel listener registration
    /// driven by the plan's
    /// <see cref="MohistHostPlan.ListenerIntent"/>, the shared
    /// <see cref="RuntimeEpoch"/>, the runtime observables seeded
    /// according to <see cref="MohistHostPlan.InitialCollectorResult"/>,
    /// Orleans, the full Mohist service graph, the request-side
    /// middleware order, and the API/Web route maps.
    /// </summary>
    public static void ApplyPlan(MohistHostPlan plan, WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(builder);

        var mainHost = builder.Configuration["urls"]
            ?? builder.Configuration["ASPNETCORE_URLS"]
            ?? $"http://{builder.Configuration["Mohist:Host"] ?? "localhost"}:" +
               (builder.Configuration.GetValue<int?>("Mohist:Port") ?? 3456);
        var mainUri = new Uri(mainHost);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            var mainAddress = ResolveBindAddress(mainUri.Host);
            kestrel.Listen(mainAddress, mainUri.Port);

            if (plan.Enabled && plan.ListenerIntent is { } listener)
            {
                var address = ResolveBindAddress(listener.BindHost);
                kestrel.Listen(address, listener.Port);
            }
        });

        builder.Services.AddSingleton(plan.Epoch);

        builder.Services.AddSingleton<RuntimeObservability>(sp =>
        {
            var time = sp.GetRequiredService<TimeProvider>();
            var logger = sp.GetService<ILogger<RuntimeObservability>>();
            var optionsAccessor = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Mohist.Server.Otel.OtelOptions>>().Value;
            var enabled = optionsAccessor.Enabled && plan.Enabled;
            return new RuntimeObservability(
                enabled,
                sp.GetRequiredService<RuntimeEpoch>(),
                time,
                logger,
                initialDegradations: ResolveSeeds(plan),
                storageBudgetBytes: RuntimeObservability.DefaultStorageBudgetBytes);
        });

        builder.Host.UseOrleans(silo => silo.ConfigureMohistSilo(builder.Configuration));
        builder.Services.AddMohistServerCore(builder.Configuration);
    }

    private static WebApplicationBuilder PrepareBuilder(WebApplicationBuilder builder)
    {
        builder.Configuration.AddMohistUserConfigFile(builder.Environment);
        builder.Logging.AddFileLogger();
        return builder;
    }

    private static WebApplicationMohistHost Build(MohistHostPlan plan, WebApplicationBuilder builder)
    {
        ApplyPlan(plan, builder);

        var app = builder.Build();

        app.UseOtelPortIsolation();
        app.UseRequestDecompression();
        app.UseResponseCompression();
        app.UseRouting();
        app.UseOtelSuppression();
        app.UseRuntimeRequestMetrics();
        app.MapMohistApi();
        app.MapMohistWeb();

        return new WebApplicationMohistHost(app);
    }

    private static IEnumerable<RuntimeDegradationSeed> ResolveSeeds(MohistHostPlan plan)
    {
        if (plan.InitialCollectorResult.IsOnline)
            return [RuntimeDegradationSeed.StorageUnverified()];

        if (plan.InitialCollectorResult.FailureCode == RuntimeDegradationCodes.CollectorBindFailed)
            return
            [
                RuntimeDegradationSeed.StorageUnverified(),
                RuntimeDegradationSeed.CollectorBindFailed(),
            ];

        return
        [
            RuntimeDegradationSeed.CollectorUnverified(),
            RuntimeDegradationSeed.StorageUnverified(),
        ];
    }

    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.Equals(host, "*", StringComparison.Ordinal) || string.Equals(host, "0.0.0.0", StringComparison.Ordinal))
            return IPAddress.Any;

        if (IPAddress.TryParse(host, out var parsed))
            return parsed;

        return IPAddress.Loopback;
    }
}

/// <summary>
/// Production <see cref="IMohistHost"/> wrapping
/// <see cref="WebApplication"/>. Stores <c>Services</c> for the runner
/// and forwards lifecycle calls to the underlying host.
/// </summary>
internal sealed class WebApplicationMohistHost : IMohistHost
{
    private readonly WebApplication _app;
    private bool _disposed;

    public WebApplicationMohistHost(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
        Services = app.Services;
    }

    public IServiceProvider Services { get; }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _app.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _app.StopAsync(cancellationToken);

    public Task WaitForShutdownAsync(CancellationToken cancellationToken) =>
        _app.WaitForShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
