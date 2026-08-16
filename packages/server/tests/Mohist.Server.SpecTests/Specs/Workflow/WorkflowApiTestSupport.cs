using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;

namespace Mohist.Server.SpecTests.Specs.Workflow;

internal static class WorkflowApiTestSupport
{
    public static async Task<(string IssueKey, int Number)> CreateIssueInBacklogAsync(
        IGrainFactory grains,
        string projectId)
    {
        var number = await grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        var grain = grains.GetGrain<IIssueGrain>(issueKey);
        await grain.CreateAsync(projectId, number, "Workflow control test", null, null, null, isDraft: false);
        return (issueKey, number);
    }

    public static Task DispatchEventsAsync(IGrainFactory grains) =>
        grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    public static async Task SeedWorkflowTemplateAsync(string connectionString, string projectId)
    {
        var definition = new WorkflowDefinition(
        [
            new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
            new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
        ]);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        const string templateId = "spec/workflow";
        var existingTemplate = await db.ProjectWorkflowTemplates.FindAsync(projectId, templateId);
        if (existingTemplate is null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = templateId,
                Template = WorkflowGrainTestHelpers.SerializeProfile(definition),
            });
        }
        else
        {
            existingTemplate.Template = WorkflowGrainTestHelpers.SerializeProfile(definition);
            existingTemplate.UpdatedAt = TestTime.UtcNow;
        }

        var profile = await db.ProjectWorkflowProfiles.FindAsync(projectId);
        if (profile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultTemplateId = templateId,
            });
        }
        else
        {
            profile.DefaultTemplateId = templateId;
            profile.UpdatedAt = TestTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public static async Task<WorkflowRun> LoadRunAsync(IServiceProvider services, string workflowRunId)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        return await store.LoadAsync(workflowRunId)
            ?? throw new InvalidOperationException($"Workflow run '{workflowRunId}' not found");
    }

    public static async Task<string> ResolveSessionIdAsync(
        IServiceProvider services,
        string workflowRunId,
        string sessionName)
    {
        await using var db = await services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        return await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
    }
}
