using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Otel;

var builder = WebApplication.CreateBuilder(args);

// 加载 ~/.mohist/config.jsonc，环境变量（MOHIST__*）会自动覆盖它
builder.Configuration.AddMohistConfigFile();

// 主 API 端口。尊重用户已经显式设置的 urls / ASPNETCORE_URLS，
// 否则使用 Mohist:Host / Mohist:Port（默认 localhost:3456）。
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]) &&
    string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]))
{
    var host = builder.Configuration["Mohist:Host"] ?? "localhost";
    var port = builder.Configuration.GetValue<int?>("Mohist:Port") ?? 3456;
    builder.WebHost.UseUrls($"http://{host}:{port}");
}

// OTel 启用时为 Kestrel 追加一个独立监听端口。绑定失败仅记录日志，
// 不阻断主 API —— spec 要求"OTLP 端口失败不阻断主 API"。
//
// Kestrel 的 Listen 调用只是把 listen option 排入队列，并不真的
// bind 套接字；真正的 bind 在 KestrelServer.StartAsync 内执行
// （即 app.StartAsync() 阶段）。因此我们必须：
//   1. 在 ConfigureKestrel 里登记 listen option；
//   2. 在 app.StartAsync() 周围用 try/catch 兜住 bind 失败；
//   3. 失败时记录日志、保持 OtelCollectorStatus = 离线，main API
//      继续运行。
var otelOptions = new OtelOptions();
builder.Configuration.GetSection(OtelOptions.SectionName).Bind(otelOptions);

if (otelOptions.Enabled)
{
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        var address = otelOptions.BindHost == "0.0.0.0" || otelOptions.BindHost == "*"
            ? System.Net.IPAddress.Any
            : System.Net.IPAddress.Loopback;
        kestrel.Listen(address, otelOptions.Port);
    });
}

builder.Host.UseOrleans(silo => silo.ConfigureMohistSilo(builder.Configuration));

builder.Services.AddMohistServerCore(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
    db.Database.Migrate();
}

// 全局拦截 OTLP 端口上的非 /otel/v1/ 路径，返回 404 防止主 API 泄漏；
// 同时拦截主端口上的 /otel/v1/ 路径，避免 SPA fallback 把它们误当成
// 静态资源。详见 OtelPortIsolationMiddleware。
app.UseOtelPortIsolation();

app.MapMohistApi();
app.MapMohistWeb(builder.Configuration);

// 启动 host 并对 OTLP 端口绑定失败做兜底：main API 端口应能继续
// 监听，而 collector 状态如实报告为离线。Kestrel 在 listen
// 失败时抛 IOException（"Failed to bind to address ...: address
// already in use."）；我们识别出这是 OTLP 端口冲突后重建一个
// 不带 OTLP listen option 的 host，让主端口独占。
var finalApp = app;
try
{
    await app.StartAsync();
    if (otelOptions.Enabled)
    {
        app.Services.GetRequiredService<OtelCollectorStatus>().SetPortBound(true);
    }
}
catch (IOException ex) when (otelOptions.Enabled && OtelBindFailureDetector.IsOtlpPortBindFailure(ex, otelOptions.Port))
{
    OtelPortBindingLog.WriteBindFailure(otelOptions.Port, otelOptions.BindHost, ex);
    // await 失败的 app 不能复用 —— Kestrel 状态可能不一致。
    // 重新构造一个禁用 OTLP 的 host。
    finalApp = BuildAlternateApp(args);
    await finalApp.StartAsync();
}
catch (Exception ex)
{
    OtelPortBindingLog.WriteGenericFailure(ex);
    throw;
}

await finalApp.WaitForShutdownAsync();

static WebApplication BuildAlternateApp(string[] args)
{
    var fresh = WebApplication.CreateBuilder(args);
    fresh.Configuration.AddMohistConfigFile();
    if (string.IsNullOrWhiteSpace(fresh.Configuration["urls"]) &&
        string.IsNullOrWhiteSpace(fresh.Configuration["ASPNETCORE_URLS"]))
    {
        var host = fresh.Configuration["Mohist:Host"] ?? "localhost";
        var port = fresh.Configuration.GetValue<int?>("Mohist:Port") ?? 3456;
        fresh.WebHost.UseUrls($"http://{host}:{port}");
    }
    // 关键：临时把 OtelOptions.Enabled 设为 false，跳过 OTLP listen。
    // OtelCollectorStatus 仍为 false（默认），/otel/api/status 会
    // 如实报告离线。
    fresh.Configuration["Mohist:Otel:Enabled"] = "false";
    fresh.Host.UseOrleans(silo => silo.ConfigureMohistSilo(fresh.Configuration));
    fresh.Services.AddMohistServerCore(fresh.Configuration);
    var alt = fresh.Build();
    using (var scope = alt.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Database.Migrate();
    }
    alt.UseOtelPortIsolation();
    alt.MapMohistApi();
    alt.MapMohistWeb(alt.Configuration);
    return alt;
}

public partial class Program { }

internal static class OtelPortBindingLog
{
    public static void WriteBindFailure(int port, string host, Exception ex)
    {
        Console.Error.WriteLine(
            $"[Mohist.Server.Otel] Failed to bind OTLP ingestion port {port} on {host}; " +
            $"collector will report offline. Main API continues normally. {ex.Message}");
    }

    public static void WriteGenericFailure(Exception ex)
    {
        Console.Error.WriteLine(
            $"[Mohist.Server.Otel] Unexpected failure during host start: {ex}");
    }
}
