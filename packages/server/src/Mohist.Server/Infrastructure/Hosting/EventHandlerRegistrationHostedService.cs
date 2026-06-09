using Microsoft.Extensions.Hosting;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Infrastructure.Hosting;

public sealed class EventHandlerRegistrationHostedService : IHostedService
{
    private readonly IEventBus _bus;

    public EventHandlerRegistrationHostedService(IEventBus bus)
    {
        _bus = bus;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [EventCatalog.ReverseDns.WorkflowRunCompleted] = typeof(IWorkflowRunCompletedHandler),
            [EventCatalog.ReverseDns.WorkflowRunStopped] = typeof(IWorkflowRunStoppedHandler),
            [EventCatalog.ReverseDns.WorkflowRunFailed] = typeof(IWorkflowRunFailedHandler),
            [EventCatalog.ReverseDns.RunnerDisconnected] = typeof(IRunnerDisconnectedHandler),
        };
        _bus.RegisterHandlerInterfaces(map);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
