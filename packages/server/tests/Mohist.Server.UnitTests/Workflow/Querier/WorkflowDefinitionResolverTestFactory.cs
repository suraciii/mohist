using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Querier;

public abstract class WorkflowDefinitionResolverTestFactory : IDisposable
{
    protected WorkflowDefinitionResolverTestFactory()
    {
        Database = TestSqliteDatabase.CreateModelSchema();
        var dbContextFactory = new TestDbContextFactory(Database.Options);
        DefinitionResolver = new WorkflowDefinitionResolver(
            dbContextFactory,
            WorkflowGrainTestHelpers.CreateEmptyConfigService(),
            new WorkflowProfileProvider(dbContextFactory, NullActionCatalogSource.Instance));
        Resolver = new WorkflowVariableResolver(
            dbContextFactory,
            new ProjectVariableStore(dbContextFactory),
            new IssueVariableStore(dbContextFactory),
            new WorkflowRunVariablesStore(dbContextFactory));
    }

    protected TestSqliteDatabase Database { get; }
    protected WorkflowDefinitionResolver DefinitionResolver { get; }
    protected WorkflowVariableResolver Resolver { get; }

    protected WorkflowDefinitionResolver CreateDefinitionResolver() =>
        new(
            new TestDbContextFactory(Database.Options),
            WorkflowGrainTestHelpers.CreateEmptyConfigService(),
            new WorkflowProfileProvider(new TestDbContextFactory(Database.Options), NullActionCatalogSource.Instance));

    public void Dispose() => Database.Dispose();

    protected static string SerializeDefinition(
        string id,
        int stageCount = 1)
    {
        var stages = new List<StageDefinition>();
        for (var i = 0; i < stageCount; i++)
            stages.Add(new StageDefinition($"stage-{i}", [], []));
        var def = new WorkflowDefinition( stages);
        return JsonSerializer.Serialize(
            new WorkflowProfile(id, id, string.Empty, def),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    protected static string SerializeDefinitionWithStages(
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

        var def = new WorkflowDefinition( stages);
        return JsonSerializer.Serialize(
            new WorkflowProfile(id, id, string.Empty, def),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    protected async Task SeedProjectTemplateAsync(string projectId, string runId, string templateId, string templateJson)
    {
        await using var db = new MohistDbContext(Database.Options);
        SeedRunContext(db, projectId, 1, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultWorkflowProfileId = templateId,
            DefaultWorkflowProfileIdKey = templateId,
            Variables = "{}",
        });
        db.WorkflowProfileRecords.Add(CreateProfileRecord(projectId, templateId, templateJson));

        await db.SaveChangesAsync();
    }

    protected async Task UpdateProjectTemplateAsync(string projectId, string templateId, string templateJson)
    {
        await using var db = new MohistDbContext(Database.Options);
        var existing = await db.WorkflowProfileRecords.FindAsync(projectId, templateId);
        Assert.NotNull(existing);
        existing!.DefinitionSource = ToDefinitionSource(templateId, templateJson);
        existing.UpdatedAt = TestTime.UtcNow;
        await db.SaveChangesAsync();
    }

    protected async Task SeedIssueOverProjectTemplateAsync(
        string projectId,
        int issueNumber,
        string runId,
        string issueTemplateJson,
        string projectDefaultTemplateId,
        string projectTemplateJson)
    {
        await using var db = new MohistDbContext(Database.Options);
        var issueProfileId = WorkflowProfilePersistence.Deserialize(issueTemplateJson).Id;
        SeedRunContext(db, projectId, issueNumber, runId, issueProfileId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultWorkflowProfileId = projectDefaultTemplateId,
            DefaultWorkflowProfileIdKey = projectDefaultTemplateId,
            Variables = "{}",
        });
        db.WorkflowProfileRecords.Add(CreateProfileRecord(projectId, projectDefaultTemplateId, projectTemplateJson));
        db.WorkflowProfileRecords.Add(CreateProfileRecord(projectId, issueProfileId, issueTemplateJson));

        await db.SaveChangesAsync();
    }

    protected async Task SeedAsync(
        string projectId, int issueNumber, string runId,
        string? issueTemplateJson,
        string? issueSourceTemplateId = null,
        string? projectDefaultTemplateId = null,
        string? projectTemplateJson = null,
        string? issueWorkflowProfileId = null,
        string[]? disabledWorkflowProfileIds = null)
    {
        await using var db = new MohistDbContext(Database.Options);
        var resolvedIssueProfileId = issueWorkflowProfileId
            ?? (issueTemplateJson is null ? issueSourceTemplateId : WorkflowProfilePersistence.Deserialize(issueTemplateJson).Id);
        SeedRunContext(db, projectId, issueNumber, runId, resolvedIssueProfileId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultWorkflowProfileId = projectDefaultTemplateId,
            DefaultWorkflowProfileIdKey = WorkflowProfileCatalog.IsSystemProfile(projectDefaultTemplateId)
                ? null
                : projectDefaultTemplateId,
            Variables = "{}",
            DisabledWorkflowProfileIds = disabledWorkflowProfileIds?.ToList() ?? [],
        });

        if (projectDefaultTemplateId is not null && projectTemplateJson is not null)
            db.WorkflowProfileRecords.Add(CreateProfileRecord(projectId, projectDefaultTemplateId, projectTemplateJson));
        if (issueSourceTemplateId is not null && projectTemplateJson is not null)
            db.WorkflowProfileRecords.Add(CreateProfileRecord(projectId, issueSourceTemplateId, projectTemplateJson));
        if (issueTemplateJson is not null)
        {
            var profile = WorkflowProfilePersistence.Deserialize(issueTemplateJson);
            db.WorkflowProfileRecords.Add(CreateProfileRecord(projectId, profile.Id, issueTemplateJson));
        }

        await db.SaveChangesAsync();
    }

    protected async Task SeedWithoutRunAsync(
        string projectId, int issueNumber,
        string? issueTemplateJson,
        string? issueSourceTemplateId = null,
        string? projectDefaultTemplateId = null,
        string? projectTemplateJson = null,
        string[]? disabledWorkflowProfileIds = null)
    {
        await using var db = new MohistDbContext(Database.Options);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultWorkflowProfileId = projectDefaultTemplateId,
            DefaultWorkflowProfileIdKey = WorkflowProfileCatalog.IsSystemProfile(projectDefaultTemplateId)
                ? null
                : projectDefaultTemplateId,
            Variables = "{}",
            DisabledWorkflowProfileIds = disabledWorkflowProfileIds?.ToList() ?? [],
        });

        if (projectDefaultTemplateId is not null && projectTemplateJson is not null)
            db.WorkflowProfileRecords.Add(CreateProfileRecord(projectId, projectDefaultTemplateId, projectTemplateJson));
        if (issueSourceTemplateId is not null && projectTemplateJson is not null)
            db.WorkflowProfileRecords.Add(CreateProfileRecord(projectId, issueSourceTemplateId, projectTemplateJson));
        if (issueTemplateJson is not null)
        {
            var profile = WorkflowProfilePersistence.Deserialize(issueTemplateJson);
            db.WorkflowProfileRecords.Add(CreateProfileRecord(projectId, profile.Id, issueTemplateJson));
        }

        await db.SaveChangesAsync();
    }

    protected async Task ReplaceRunStateAsync(string runId, string projectId, int issueNumber, string systemProfileId)
    {
        await using var db = new MohistDbContext(Database.Options);
        var row = await db.WorkflowRuns.FirstAsync(x => x.WorkflowRunId == runId);
        var definition = WorkflowProfileCatalog.GetDefinition(systemProfileId)
            ?? throw new InvalidOperationException($"Unknown system profile '{systemProfileId}'");
        var run = WorkflowRun.Create(
            runId,
            definition,
            DateTimeOffset.UnixEpoch,
            new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UnixEpoch,
                ProjectId: projectId,
                IssueNumber: issueNumber));
        row.State = JSON.Serialize(run);
        await db.SaveChangesAsync();
    }

    protected async Task ReplaceRunStateJsonAsync(string runId, string stateJson)
    {
        await using var db = new MohistDbContext(Database.Options);
        var row = await db.WorkflowRuns.FirstAsync(x => x.WorkflowRunId == runId);
        row.State = stateJson;
        await db.SaveChangesAsync();
    }

    protected async Task SeedRunOnlyAsync(
        string projectId, int issueNumber, string runId)
    {
        await using var db = new MohistDbContext(Database.Options);
        SeedRunContext(db, projectId, issueNumber, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            Variables = "{}",
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    protected async Task SeedAllLayersAsync(
        string projectId, int issueNumber, string runId,
        VariableBundle project,
        VariableBundle issue,
        string? issueTemplateJson = null,
        VariableBundle? runtime = null)
    {
        await using var db = new MohistDbContext(Database.Options);
        SeedRunContext(db, projectId, issueNumber, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            Variables = project.ToJson(),
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
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

    protected async Task SeedIssueOnlyAsync(
        string projectId, int issueNumber, string runId, VariableBundle issue)
    {
        await using var db = new MohistDbContext(Database.Options);
        SeedRunContext(db, projectId, issueNumber, runId);

        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            Variables = issue.ToJson(),
        });

        await db.SaveChangesAsync();
    }

    protected static void SeedRunContext(
        MohistDbContext db,
        string projectId,
        int issueNumber,
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
                    CreatedAt = TestTime.UtcNow,
                    ProjectId = projectId,
                    IssueNumber = issueNumber,
                },
                Status = "Failed",
                Stages = Array.Empty<object>(),
            }),
        });
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            State = JSON.Serialize(new
            {
                ProjectId = projectId,
                Number = issueNumber,
                Title = "Seeded issue",
                Priority = "p2",
                WorkflowRunId = runId,
                WorkflowProfileId = issueWorkflowProfileId,
            }),
        });
    }

    private static WorkflowProfileRecordRow CreateProfileRecord(string projectId, string profileId, string serializedProfile)
    {
        var profile = WorkflowProfilePersistence.Deserialize(serializedProfile);
        return new WorkflowProfileRecordRow
        {
            ProjectId = projectId,
            ProfileId = profileId,
            Name = profile.Name,
            Description = profile.Description,
            DefinitionSource = ToDefinitionSource(profileId, serializedProfile),
            SourceProvenance = nameof(WorkflowProfileSourceProvenance.CanonicalLegacy),
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        };
    }

    private static string ToDefinitionSource(string profileId, string serializedProfile) =>
        WorkflowProfileCanonicalYamlRenderer.Render(
            WorkflowProfilePersistence.Deserialize(serializedProfile) with { Id = profileId });
}
