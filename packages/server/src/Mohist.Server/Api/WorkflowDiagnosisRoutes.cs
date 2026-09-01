using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static class WorkflowDiagnosisRoutes
{
    public static WebApplication MapWorkflowDiagnosisRoutes(this WebApplication app)
    {
        app.MapGet("/api/runs/{runRef}/diagnosis", async (
            string runRef,
            int? limit,
            DiagnosisAssembler diagnosis,
            CancellationToken ct) =>
        {
            var result = await diagnosis.AssembleAsync(
                runRef,
                limit ?? DiagnosisAssembler.DefaultEventLimit,
                ct);

            return result is null
                ? ApiResults.NotFound($"Workflow run '{runRef}' not found")
                : ApiResults.Ok(result);
        }).RequireScopes(Scope.Operator);

        return app;
    }
}
