using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.UnitTests.Workflow.Querier;

internal sealed class WorkflowProfileManagerTestContext : IDisposable
{
    private readonly SqliteConnection _keeper;

    public WorkflowProfileManagerTestContext()
    {
        var connectionString = $"Data Source=profile-specs-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        Options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;

        var factory = CreateDbContextFactory();
        var promptLoader = new FilePromptLoader();
        var registry = new Mohist.Server.Issue.Services.WorkflowProfiles.IssueWorkflowProfileRegistry(promptLoader, factory);
        Manager = new WorkflowProfileManager(
            factory,
            promptLoader,
            new PromptTemplateEngine(),
            WorkflowGrainTestHelpers.CreateEmptyConfigService(),
            new WorkflowRunProfileManager(factory),
            new Mohist.Server.Issue.Services.WorkflowProfiles.EffectiveWorkflowProfileResolver(registry));

        MigratedSqliteTemplate.CopyTo(_keeper);
    }

    public DbContextOptions<MohistDbContext> Options { get; }
    public WorkflowProfileManager Manager { get; }

    public void Dispose() => _keeper.Dispose();

    public IDbContextFactory<MohistDbContext> CreateDbContextFactory() => new TestDbContextFactory(Options);

    public string SerializeDefinition(
        string id,
        int stageCount = 1,
        Dictionary<string, JsonElement?>? variables = null)
    {
        var stages = new List<StageDefinition>();
        for (var i = 0; i < stageCount; i++)
            stages.Add(new StageDefinition($"stage-{i}", [], []));
        var definition = new WorkflowDefinition(id, stages, Variables: variables);
        return JsonSerializer.Serialize(definition, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    public string SerializeDefinitionWithStages(
        string id,
        params (string stage, TaskDefinition[] tasks, CheckDefinition[] checks, bool requiresApproval)[] stageSpecs)
    {
        var stages = new List<StageDefinition>();
        foreach (var (stage, tasks, checks, requiresApproval) in stageSpecs)
        {
            stages.Add(new StageDefinition(
                stage,
                new List<TaskDefinition>(tasks),
                new List<CheckDefinition>(checks),
                RequiresApproval: requiresApproval,
                LockBehavior: stage == "build" ? "sequential" : null,
                Resources: stage == "build" ? new List<string> { "ci-pool" } : null));
        }

        var definition = new WorkflowDefinition(id, stages);
        return JsonSerializer.Serialize(definition, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    public async Task SeedProjectTemplateAsync(string projectId, string runId, string templateId, string templateJson)
    {
        await using var db = new MohistDbContext(Options);
        SeedRunContext(db, projectId, runId, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = templateId,
            Variables = "{}",
        });
        db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
        {
            ProjectId = projectId,
            TemplateId = templateId,
            Template = templateJson,
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            IssueId = runId,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    public async Task UpdateProjectTemplateAsync(string projectId, string templateId, string templateJson)
    {
        await using var db = new MohistDbContext(Options);
        var existing = await db.ProjectWorkflowTemplates.FindAsync(projectId, templateId)
            ?? throw new InvalidOperationException($"Missing project workflow template '{templateId}'.");
        existing.Template = templateJson;
        existing.UpdatedAt = DateTimeOffset.UnixEpoch;
        await db.SaveChangesAsync();
    }

    public async Task SeedIssueOverProjectTemplateAsync(
        string projectId,
        string issueId,
        string runId,
        string issueTemplateJson,
        string projectDefaultTemplateId,
        string projectTemplateJson)
    {
        await using var db = new MohistDbContext(Options);
        SeedRunContext(db, projectId, issueId, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = projectDefaultTemplateId,
            Variables = "{}",
        });
        db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
        {
            ProjectId = projectId,
            TemplateId = projectDefaultTemplateId,
            Template = projectTemplateJson,
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            IssueId = issueId,
            Template = issueTemplateJson,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    public async Task SeedAsync(
        string projectId,
        string issueId,
        string runId,
        string? issueTemplateJson,
        string? issueSourceTemplateId = null,
        string? projectDefaultTemplateId = null,
        string? projectTemplateJson = null,
        string? issueWorkflowProfileId = null,
        string[]? disabledWorkflowProfileIds = null)
    {
        await using var db = new MohistDbContext(Options);
        SeedRunContext(db, projectId, issueId, runId, issueWorkflowProfileId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = projectDefaultTemplateId,
            Variables = "{}",
            DisabledWorkflowProfileIds = disabledWorkflowProfileIds?.ToList() ?? [],
        });

        if (projectDefaultTemplateId is not null && projectTemplateJson is not null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = projectDefaultTemplateId,
                Template = projectTemplateJson,
            });
        }

        if (issueSourceTemplateId is not null && projectTemplateJson is not null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = issueSourceTemplateId,
                Template = projectTemplateJson,
            });
        }

        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            IssueId = issueId,
            SourceTemplateId = issueSourceTemplateId,
            Template = issueTemplateJson,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    public async Task SeedWithoutRunAsync(
        string projectId,
        string issueId,
        string? issueTemplateJson,
        string? issueSourceTemplateId = null,
        string? projectDefaultTemplateId = null,
        string? projectTemplateJson = null,
        string[]? disabledWorkflowProfileIds = null)
    {
        await using var db = new MohistDbContext(Options);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = projectDefaultTemplateId,
            Variables = "{}",
            DisabledWorkflowProfileIds = disabledWorkflowProfileIds?.ToList() ?? [],
        });

        if (projectDefaultTemplateId is not null && projectTemplateJson is not null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = projectDefaultTemplateId,
                Template = projectTemplateJson,
            });
        }

        if (issueSourceTemplateId is not null && projectTemplateJson is not null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = issueSourceTemplateId,
                Template = projectTemplateJson,
            });
        }

        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            IssueId = issueId,
            SourceTemplateId = issueSourceTemplateId,
            Template = issueTemplateJson,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    public async Task SeedProjectOnlyAsync(string projectId, string issueId, string templateId, string templateJson)
    {
        await using var db = new MohistDbContext(Options);
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = templateId,
            Variables = "{}",
        });
        db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
        {
            ProjectId = projectId,
            TemplateId = templateId,
            Template = templateJson,
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            IssueId = issueId,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    public async Task ReplaceRunStateAsync(string runId, string projectId, string issueId, string systemProfileId)
    {
        await using var db = new MohistDbContext(Options);
        var row = await db.WorkflowRuns.FirstAsync(x => x.WorkflowRunId == runId);
        var definition = ProjectWorkflowProfileManager.GetSystemTemplateDefinition(systemProfileId)
            ?? throw new InvalidOperationException($"Unknown system profile '{systemProfileId}'");
        var run = WorkflowRun.Create(
            runId,
            definition,
            DateTimeOffset.UnixEpoch,
            new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UnixEpoch,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = projectId,
                    ["issueId"] = issueId,
                }));
        row.State = JSON.Serialize(run);
        await db.SaveChangesAsync();
    }

    public async Task ReplaceRunStateJsonAsync(string runId, string stateJson)
    {
        await using var db = new MohistDbContext(Options);
        var row = await db.WorkflowRuns.FirstAsync(x => x.WorkflowRunId == runId);
        row.State = stateJson;
        await db.SaveChangesAsync();
    }

    public async Task SeedAllLayersAsync(
        string projectId,
        string issueId,
        string runId,
        VariableBundle project,
        VariableBundle issue,
        string? issueTemplateJson = null,
        VariableBundle? runtime = null)
    {
        await using var db = new MohistDbContext(Options);
        SeedRunContext(db, projectId, issueId, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            Variables = project.ToJson(),
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            IssueId = issueId,
            Template = issueTemplateJson,
            Variables = issue.ToJson(),
        });
        if (runtime is not null)
        {
            db.WorkflowRunProfiles.Add(new WorkflowRunProfileRow
            {
                WorkflowRunId = runId,
                Variables = runtime.ToJson(),
            });
        }

        await db.SaveChangesAsync();
    }

    public async Task SeedIssueOnlyAsync(string projectId, string issueId, string runId, VariableBundle issue)
    {
        await using var db = new MohistDbContext(Options);
        SeedRunContext(db, projectId, issueId, runId);

        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            IssueId = issueId,
            Variables = issue.ToJson(),
        });

        await db.SaveChangesAsync();
    }

    private static void SeedRunContext(
        MohistDbContext db,
        string projectId,
        string issueId,
        string runId,
        string? issueWorkflowProfileId = null)
    {
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = JSON.Serialize(new
            {
                Id = runId,
                Metadata = new
                {
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    Annotations = new Dictionary<string, string> { ["issueId"] = issueId },
                },
                Status = "Failed",
                Stages = Array.Empty<object>(),
            }),
        });
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            State = JSON.Serialize(new
            {
                Id = issueId,
                ProjectId = projectId,
                Number = 1,
                Title = "Seeded issue",
                Priority = "p2",
                WorkflowRunId = runId,
                WorkflowProfileId = issueWorkflowProfileId,
            }),
        });
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options) : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
