using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Tests.Specs.Issue.Profile;
using Orleans.TestingHost;

namespace Mohist.Server.Tests.Support;

public static class GrainTestConfig
{
    public static MohistDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(connectionString).Options;
        return new MohistDbContext(options);
    }

    public static void ConfigureSilo(
        ISiloBuilder siloBuilder,
        string connectionString,
        IEventPublisher eventBus,
        IEventStore eventStore)
    {
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.AddMemoryGrainStorageAsDefault();
        siloBuilder.Services.AddDbContextFactory<MohistDbContext>(options => options.UseSqlite(connectionString));
        siloBuilder.Services.AddScoped<IWorkflowRunStore, WorkflowRunStore>();
        siloBuilder.Services.AddSingleton<ProjectQuerier>();
        siloBuilder.Services.AddSingleton<IPromptLoader>(_ => new FakePromptLoader());
        siloBuilder.Services.AddSingleton<PromptTemplateEngine>();
        siloBuilder.Services.AddScoped<WorkflowProfileManager>();
        siloBuilder.Services.AddScoped<IssueWorkflowProfileRegistry>();
        siloBuilder.Services.AddSingleton<IWorkflowBacklogDirectory, InMemoryWorkflowBacklogDirectory>();
        siloBuilder.Services.AddSingleton(eventBus);
        siloBuilder.Services.AddSingleton(eventStore);
        siloBuilder.Services.AddScoped<IWorkflowArtifactBindService, WorkflowArtifactBindService>();
        siloBuilder.Services.AddScoped<AgentSessionQuery>();
        siloBuilder.Services.AddScoped<AgentSessionResolver>();
    }
}
