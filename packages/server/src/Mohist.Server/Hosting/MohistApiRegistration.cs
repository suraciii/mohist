using Microsoft.EntityFrameworkCore;
using Mohist.Server.Api;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Hosting;

public static class MohistApiRegistration
{
    public static WebApplication EnsureMohistDatabase(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Database.EnsureCreated();
        return app;
    }

    public static WebApplication MapMohistApi(this WebApplication app)
    {
        app.UseApiExceptionHandler();
        app.MapHealthRoutes();
        app.MapStatusRoutes();
        app.MapProjectRoutes();
        app.MapIssueRoutes();
        app.MapWorkflowEventRoutes();
        app.MapEventRoutes();
        app.MapConfigRoutes();
        app.MapProvidersRoutes();
        app.MapLabelsRoutes();
        app.MapLogsRoutes();
        app.MapFsRoutes();
        app.MapWorkspaceRoutes();
        app.MapAgentRoutes();
        app.MapRunnerRoutes();
        return app;
    }
}
