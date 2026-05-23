using Microsoft.EntityFrameworkCore;
using Mohist.Server.Api;
using Mohist.Server.Config.Domain;
using Mohist.Server.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workspace;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseOrleans(silo =>
{
    silo.ConfigureLogging(logging =>
    {
        logging.AddConsole();
    });
});

var home = Environment.GetEnvironmentVariable("HOME")
    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var dataDir = Path.Combine(home, ".mohist");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "mohist.db");

builder.Services.AddDbContextFactory<MohistDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddScoped(typeof(IStateStore<>), typeof(EfStateStore<>));
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddScoped<ConfigService>();
builder.Services.AddSingleton<IGitService, GitService>();

var app = builder.Build();

// Ensure database schema is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
    db.Database.EnsureCreated();
}

app.UseApiExceptionHandler();
app.MapHealthRoutes();
app.MapStatusRoutes();
app.MapProjectRoutes();
app.MapIssueRoutes();
app.MapEventRoutes();
app.MapConfigRoutes();
app.MapProvidersRoutes();
app.MapLabelsRoutes();
app.MapLogsRoutes();
app.MapFsRoutes();
app.MapWorkspaceRoutes();
app.MapRunnerRoutes();

app.Run();
