using Mohist.Server.Api;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseOrleans(silo =>
{
    silo.ConfigureLogging(logging =>
    {
        logging.AddConsole();
    });
});

builder.Services.AddSingleton(typeof(IStateStore<>), typeof(InMemoryStateStore<>));
builder.Services.AddSingleton<IRunnerRegistry, RunnerRegistry>();

var app = builder.Build();

app.MapIssueRoutes();
app.MapRunnerRoutes();

app.Run();
