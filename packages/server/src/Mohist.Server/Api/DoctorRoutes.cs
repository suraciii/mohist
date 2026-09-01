using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.SystemInfo;

namespace Mohist.Server.Api;

public static class DoctorRoutes
{
    public static WebApplication MapDoctorRoutes(this WebApplication app)
    {
        app.MapGet("/api/doctor/checks", async (DoctorCheckService doctor, CancellationToken ct) =>
            ApiResults.Ok(await doctor.GetChecksAsync(ct)))
            .RequireScopes(Scope.Operator);

        return app;
    }
}
