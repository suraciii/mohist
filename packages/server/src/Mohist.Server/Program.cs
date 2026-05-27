using Mohist.Server.Config;
using Mohist.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);

// 加载 ~/.mohist/config.jsonc，环境变量（MOHIST__*）会自动覆盖它
builder.Configuration.AddMohistConfigFile();

if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]) &&
    string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]))
{
    var host = builder.Configuration["Mohist:Host"] ?? "localhost";
    var port = builder.Configuration.GetValue<int?>("Mohist:Port") ?? 3456;
    builder.WebHost.UseUrls($"http://{host}:{port}");
}

builder.Host.UseOrleans(silo => silo.ConfigureMohistSilo());

builder.Services.AddMohistServerCore(builder.Configuration);

var app = builder.Build();
app.EnsureMohistDatabase();
app.MapMohistApi();
app.MapMohistWeb(builder.Configuration);

app.Run();

public partial class Program { }
