using Mohist.Runner;
using Mohist.Runner.Actions;
using Mohist.Runner.Handlers;
using Mohist.Runner.Transport;

var builder = Host.CreateApplicationBuilder(args);

var serverUrl = builder.Configuration["ServerUrl"] ?? "http://localhost:3456";
var runnerId = builder.Configuration["RunnerId"] ?? $"runner-{Environment.MachineName}-{Environment.ProcessId}";

builder.Services.AddHttpClient<IServerConnection>((sp, client) =>
{
    client.BaseAddress = new Uri(serverUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<IServerConnection>(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var http = httpFactory.CreateClient(nameof(IServerConnection));
    return new HttpServerConnection(http, runnerId, sp.GetRequiredService<ILogger<HttpServerConnection>>());
});
builder.Services.AddSingleton<ISessionTelemetrySink>(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var http = httpFactory.CreateClient(nameof(IServerConnection));
    return new HttpSessionTelemetrySink(http, runnerId);
});

builder.Services.AddSingleton<ActionManager>(sp =>
{
    var manager = new ActionManager(sp, sp.GetRequiredService<ILogger<ActionManager>>());
    RunnerActionCatalog.RegisterDefaults(manager, sp);
    return manager;
});

builder.Services.AddSingleton<RunnerHostOptions>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAgentExecutor>(sp =>
    new ProcessAgentExecutor(sp.GetRequiredService<ILogger<ProcessAgentExecutor>>()));
builder.Services.AddSingleton<IWorkspaceManager>(sp =>
    new WorkspaceManager(sp.GetRequiredService<ILogger<WorkspaceManager>>()));
builder.Services.AddSingleton<IWorkExecutor, WorkExecutor>();
builder.Services.AddSingleton<RunnerHost>();

var host = builder.Build();

var runner = host.Services.GetRequiredService<RunnerHost>();
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await runner.RunAsync(cts.Token);
