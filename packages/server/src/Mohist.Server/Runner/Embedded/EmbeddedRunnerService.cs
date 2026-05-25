using Mohist.Runner;
using Mohist.Runner.Actions;
using Mohist.Runner.Handlers;
using Mohist.Runner.Transport;

namespace Mohist.Server.Runner.Embedded;

public sealed class EmbeddedRunnerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmbeddedRunnerService> _log;

    public EmbeddedRunnerService(IServiceProvider services, IConfiguration configuration, ILogger<EmbeddedRunnerService> log)
    {
        _services = services;
        _configuration = configuration;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled(_configuration))
            return;

        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var runnerId = _configuration["Mohist:EmbeddedRunner:Id"] ?? $"embedded-{Environment.MachineName}-{Environment.ProcessId}";

        var connection = new EmbeddedRunnerConnection(
            sp.GetRequiredService<IGrainFactory>(),
            sp.GetRequiredService<Sessions.AgentSessionService>(),
            sp.GetRequiredService<ILogger<EmbeddedRunnerConnection>>(),
            runnerId);
        var actionManager = new ActionManager(sp, sp.GetRequiredService<ILogger<ActionManager>>());
        RunnerActionCatalog.RegisterDefaults(actionManager, sp);

        var executor = new WorkExecutor(
            actionManager,
            sp.GetRequiredService<ILogger<WorkExecutor>>(),
            sp.GetRequiredService<IWorkspaceManager>());
        var host = new RunnerHost(
            connection,
            executor,
            sp.GetRequiredService<ILogger<RunnerHost>>(),
            TimeProvider.System,
            new RunnerHostOptions());

        _log.LogInformation("Starting embedded runner {RunnerId}", runnerId);
        await host.RunAsync(stoppingToken);
    }

    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool?>("Mohist:EmbeddedRunner:Enabled") ?? true;
}
