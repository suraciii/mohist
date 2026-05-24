using Mohist.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);

var useExternalOrleans = builder.Configuration.GetValue<bool>("Mohist:UseExternalOrleans");

if (!useExternalOrleans)
{
    builder.Host.UseOrleans(silo => silo.ConfigureMohistSilo());
}

builder.Services.AddMohistServerCore(builder.Configuration);

var app = builder.Build();
app.EnsureMohistDatabase();
app.MapMohistApi();
app.MapMohistWeb(builder.Configuration);

app.Run();

public partial class Program { }
