using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.SystemInfo;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static class SystemRoutes
{
    public static WebApplication MapSystemRoutes(this WebApplication app)
    {
        app.MapGet("/api/system/info", async (SystemInfoService systemInfo, CancellationToken ct) =>
            ApiResults.Ok(await systemInfo.GetSystemInfoAsync()));

        app.MapGet("/api/workflow-templates/system", async (ProjectWorkflowProfileManager profileManager) =>
            ApiResults.Ok(await profileManager.ListSystemTemplatesAsync()));

        app.MapGet("/api/workflow-profiles", (IssueWorkflowProfileRegistry registry) =>
            ApiResults.Ok(registry.ListDescribed()));

        app.MapGet("/api/workflow-templates/system/{*id}", (string id) =>
        {
            var definition = ProjectWorkflowProfileManager.GetSystemTemplateDefinition(id);
            if (definition is null)
                return ApiResults.NotFound($"Workflow template '{id}' not found");

            var template = ProjectWorkflowProfileManager.GetSystemTemplateInfo(id)
                ?? new SystemTemplateInfo(id, id, "No description provided", false);

            return ApiResults.Ok(new SystemWorkflowTemplateDetail(
                id,
                template.Name,
                template.Description,
                template.IsDefault,
                WorkflowYamlSerializer.ToYaml(definition),
                definition.Stages.Select(s => new SystemWorkflowTemplateStageSummary(
                    s.Stage,
                    s.RequiresApproval,
                    s.Tasks.Select(t => t.Id).ToList(),
                    s.Checks.Select(c => c.Name).ToList())).ToList()));
        });

        app.MapPost("/api/system/update", async (SystemUpdateRequest? request, SystemUpdateService updates, CancellationToken ct) =>
        {
            var result = await updates.StartAsync(request ?? new SystemUpdateRequest(), ct);
            if (!result.Started)
            {
                return result.Code == "update_in_progress"
                    ? ApiResults.Conflict(result.Error ?? "A system update is already in progress", result.Code)
                    : ApiResults.Fail(result.Error ?? "System update failed", 400, result.Code);
            }

            return Results.Json(
                new ApiResponse<SystemUpdateStartResponse>(
                    true,
                    new SystemUpdateStartResponse(result.Status!)),
                statusCode: 202);
        });

        app.MapGet("/api/system/update/status", async (SystemUpdateService updates, CancellationToken ct) =>
        {
            return ApiResults.Ok(await updates.GetStatusEnvelopeAsync(ct));
        });

        app.MapPost("/api/system/update/outcome", async (SystemUpdateOutcomeRequest? request, SystemUpdateService updates, CancellationToken ct) =>
        {
            var payload = request ?? new SystemUpdateOutcomeRequest();
            if (string.IsNullOrWhiteSpace(payload.Status) || string.IsNullOrWhiteSpace(payload.Outcome))
            {
                return ApiResults.BadRequest("status and outcome are required", "invalid_outcome");
            }

            try
            {
                var response = await updates.RecordCliOutcomeAsync(payload, ct);
                return ApiResults.Ok(new SystemUpdateOutcomeResponse(response));
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_outcome");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message, "job_id_mismatch");
            }
        });

        app.MapGet("/api/system/consistency", async (SystemUpdateService updates, CancellationToken ct) =>
        {
            return ApiResults.Ok(await updates.GetConsistencyAsync(ct));
        });

        return app;
    }
}

public sealed record SystemWorkflowTemplateDetail(
    string Id,
    string DisplayName,
    string Description,
    bool IsDefault,
    string Yaml,
    List<SystemWorkflowTemplateStageSummary> Stages);

public sealed record SystemWorkflowTemplateStageSummary(
    string Stage,
    bool RequiresApproval,
    List<string> Tasks,
    List<string> Checks);
