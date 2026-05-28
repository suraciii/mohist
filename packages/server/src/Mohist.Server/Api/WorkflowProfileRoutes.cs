using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Workflow.Infrastructure;

namespace Mohist.Server.Api;

public static class WorkflowProfileRoutes
{
    public static WebApplication MapWorkflowProfileRoutes(this WebApplication app)
    {
        app.MapGet("/api/workflow-profiles", (IssueWorkflowProfileRegistry profiles) =>
            ApiResults.Ok(profiles.List()));

        app.MapGet("/api/workflow-profiles/{*id}", (string id, IssueWorkflowProfileRegistry profiles) =>
        {
            if (!profiles.Exists(id))
                return ApiResults.NotFound($"Workflow profile '{id}' not found");

            var profile = profiles.Get(id);
            var yaml = WorkflowYamlSerializer.ToYaml(profile.Definition);
            return ApiResults.Ok(new WorkflowProfileDetail(
                profile.Id,
                profile.DisplayName,
                profile.Description,
                profile.IsDefault,
                yaml,
                profile.Definition.Stages.Select(s => new WorkflowProfileStageSummary(
                    s.Stage,
                    s.RequiresApproval,
                    s.Tasks.Select(t => t.Id).ToList(),
                    s.Checks.Select(c => c.Name).ToList())).ToList()));
        });

        return app;
    }
}

public sealed record WorkflowProfileDetail(
    string Id,
    string DisplayName,
    string Description,
    bool IsDefault,
    string Yaml,
    List<WorkflowProfileStageSummary> Stages);

public sealed record WorkflowProfileStageSummary(
    string Stage,
    bool RequiresApproval,
    List<string> Tasks,
    List<string> Checks);
