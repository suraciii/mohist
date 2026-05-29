using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Storage;

public interface IWorkflowRunStore
{
    Task SaveAsync(WorkflowRun run);
    Task<WorkflowRun?> LoadAsync(string workflowRunId);
}

public class WorkflowRunStore : IWorkflowRunStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkflowRunStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task SaveAsync(WorkflowRun run)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.WorkflowRuns.FindAsync(run.Id);

        var json = JsonSerializer.Serialize(run, JsonOptions);
        var indexFields = ExtractIndexFields(run);

        if (entity is null)
        {
            entity = new WorkflowRunEntity { WorkflowRunId = run.Id };
            ApplyIndexFields(entity, json, indexFields);
            db.WorkflowRuns.Add(entity);
        }
        else
        {
            ApplyIndexFields(entity, json, indexFields);
            db.WorkflowRuns.Update(entity);
        }

        await db.SaveChangesAsync();
    }

    public async Task<WorkflowRun?> LoadAsync(string workflowRunId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.WorkflowRuns.FindAsync(workflowRunId);
        if (entity is null) return null;
        return Deserialize(entity.State);
    }

    private static WorkflowRun? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowRun>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IndexFields ExtractIndexFields(WorkflowRun run)
    {
        var projectId = run.Metadata.Annotations?.GetValueOrDefault("projectId");
        var definitionId = run.Metadata.Annotations?.GetValueOrDefault("definitionId");

        return new IndexFields
        {
            Name = run.Metadata.Name,
            ProjectId = projectId,
            DefinitionId = definitionId,
            CreatedAt = run.Metadata.CreatedAt.ToUnixTimeMilliseconds(),
            Labels = JsonSerializer.Serialize(run.Metadata.Labels ?? new Dictionary<string, string>()),
            Phase = run.Phase.ToString(),
            CurrentStageId = run.CurrentStageId,
            PhaseUpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    private static void ApplyIndexFields(WorkflowRunEntity entity, string json, IndexFields fields)
    {
        entity.State = json;
        entity.MetadataName = fields.Name;
        entity.MetadataProjectId = fields.ProjectId;
        entity.MetadataDefinitionId = fields.DefinitionId;
        entity.MetadataCreatedAt = fields.CreatedAt;
        entity.MetadataLabels = fields.Labels;
        entity.Phase = fields.Phase;
        entity.CurrentStageId = fields.CurrentStageId;
        entity.PhaseUpdatedAt = fields.PhaseUpdatedAt;
    }

    private record IndexFields
    {
        public string? Name { get; init; }
        public string? ProjectId { get; init; }
        public string? DefinitionId { get; init; }
        public long CreatedAt { get; init; }
        public string Labels { get; init; } = "{}";
        public string Phase { get; init; } = "Pending";
        public string? CurrentStageId { get; init; }
        public long PhaseUpdatedAt { get; init; }
    }
}
