using Mohist.Runner;
using Mohist.Runner.Handlers;
using Mohist.Runner.Transport;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<ActionManager>(sp =>
{
    var manager = new ActionManager(sp, sp.GetRequiredService<ILogger<ActionManager>>());
    manager.Register("mohist/process", () => new ProcessHandler(sp.GetRequiredService<ILogger<ProcessHandler>>()));
    manager.Register("mohist/script", () => new ScriptHandler(sp.GetRequiredService<ILogger<ScriptHandler>>()));
    return manager;
});

builder.Services.AddSingleton<IServerConnection>(sp =>
    throw new InvalidOperationException("Register an IServerConnection implementation (e.g., HttpServerConnection)"));

builder.Services.AddSingleton<RunnerHost>();

var host = builder.Build();

var runner = host.Services.GetRequiredService<RunnerHost>();
var ct = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    ct.Cancel();
};

await runner.RunAsync(ct.Token);
