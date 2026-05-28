using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Project.Queries;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Storage;

public sealed class IssueWorkflowProfileSnapshot
{
    public string SourceProfileId { get; set; } = IssueWorkflowProfiles.DefaultId;
    public WorkflowDefinition Definition { get; set; } = null!;

    public IssueWorkflowProfile ToDomain() => new(SourceProfileId, Definition);

    public static IssueWorkflowProfileSnapshot FromDomain(IssueWorkflowProfile profile) => new()
    {
        SourceProfileId = profile.SourceProfileId,
        Definition = profile.Definition,
    };
}

public sealed class IssueSnapshot
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string? Body { get; set; }
    public string[] Labels { get; set; } = [];
    public string Priority { get; set; } = "p2";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public string? WorkflowRunId { get; set; }
    [JsonPropertyName("Status")]
    public IssueStage Stage { get; set; } = IssueStage.Backlog;
    public IssueAttention? Attention { get; set; }
    [JsonPropertyName("ApprovalState")]
    public StageApproval? StageApproval { get; set; }
    public int RetryCount { get; set; }
    public int ConflictRetryCount { get; set; }
    public string? BlockedReason { get; set; }
    public int[] PrerequisiteNumbers { get; set; } = [];
    public RepositoryInfo? Repository { get; set; }
    public IssueWorkflowProfileSnapshot? WorkflowProfile { get; set; }

    [JsonPropertyName("Stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? LegacyStage { get; set; }

    [JsonPropertyName("RuntimeStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? LegacyRuntimeStatus { get; set; }

    #region Legacy fields (read for migration, no longer written)
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? AgentConfig { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? StageModels { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, Dictionary<string, string>>? StageVariables { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowProfileId { get; set; }
    #endregion

    public static IssueSnapshot FromDomain(Domain.Issue issue, IssueWorkflowProfile? profile) => new()
    {
        Id = issue.Id,
        ProjectId = issue.ProjectId,
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        Labels = issue.Labels,
        Priority = issue.Priority,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ArchivedAt = issue.ArchivedAt,
        WorkflowRunId = issue.WorkflowRunId,
        Stage = issue.Stage,
        Attention = issue.Attention,
        StageApproval = issue.StageApproval,
        RetryCount = issue.RetryCount,
        ConflictRetryCount = issue.ConflictRetryCount,
        BlockedReason = issue.BlockedReason,
        PrerequisiteNumbers = issue.PrerequisiteNumbers,
        Repository = issue.Repository,
        WorkflowProfile = profile is not null ? IssueWorkflowProfileSnapshot.FromDomain(profile) : null,
    };

    public (Domain.Issue Issue, IssueWorkflowProfile? Profile) ToDomain()
    {
        var stage = LegacyStage is { } legacyStage ? LegacyStageName(legacyStage) : Stage;
        var attention = Attention;
        var blockedReason = BlockedReason;
        var approvalState = StageApproval;

        if (LegacyRuntimeStatus is { } legacyRuntimeStatus)
        {
            var runtime = LegacyName(legacyRuntimeStatus, ["active", "paused", "blocked", "interrupted", "closed", "completed"]);
            if (runtime == "closed")
            {
                stage = IssueStage.Cancelled;
                attention = null;
                blockedReason = null;
            }
            else if (runtime == "completed")
            {
                stage = IssueStage.Done;
                attention = null;
                blockedReason = null;
                approvalState = null;
            }
            else if (runtime == "blocked" && attention is null)
            {
                attention = IssueAttention.Blocked(WorkflowRunId, blockedReason);
            }
        }

        var issue = Domain.Issue.Restore(
            Id,
            ProjectId,
            Number,
            Title,
            Body,
            Labels,
            Priority,
            CreatedAt == default ? DateTime.UtcNow : CreatedAt,
            UpdatedAt == default ? DateTime.UtcNow : UpdatedAt,
            ArchivedAt,
            WorkflowRunId,
            stage,
            attention,
            approvalState,
            RetryCount,
            ConflictRetryCount,
            blockedReason,
            PrerequisiteNumbers,
            Repository);

        var profile = MigrateProfile();
        return (issue, profile);
    }

    private IssueWorkflowProfile? MigrateProfile()
    {
        if (WorkflowProfile?.Definition is not null)
            return WorkflowProfile.ToDomain();

        var hasLegacyData = Model is not null
            || AgentConfig is not null
            || StageModels is not null
            || StageVariables is not null
            || WorkflowProfileId is not null;

        if (!hasLegacyData)
            return null;

        var profileId = !string.IsNullOrWhiteSpace(WorkflowProfileId) ? WorkflowProfileId : IssueWorkflowProfiles.DefaultId;
        var definition = MohistWorkflow.Definition;

        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        if (definition.Variables is not null)
            foreach (var (k, v) in definition.Variables)
                variables[k] = v;

        var agentConfig = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "opencode" };
        if (AgentConfig is not null)
            foreach (var (k, v) in AgentConfig)
                if (v is not null) agentConfig[k] = v;
        if (!string.IsNullOrWhiteSpace(Model))
            agentConfig["model"] = Model;
        if (agentConfig.Count > 0)
            variables["agent"] = JsonSerializer.SerializeToElement(agentConfig, WorkflowVariableJson.Options);

        var stages = definition.Stages.Select(stage =>
        {
            var stageVars = stage.Variables != null
                ? new Dictionary<string, JsonElement?>(stage.Variables, StringComparer.Ordinal)
                : new Dictionary<string, JsonElement?>(StringComparer.Ordinal);

            if (StageModels is not null && StageModels.TryGetValue(stage.Stage, out var stageModel) && !string.IsNullOrWhiteSpace(stageModel))
                stageVars["agent"] = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?> { ["model"] = stageModel }, WorkflowVariableJson.Options);

            if (StageVariables is not null && StageVariables.TryGetValue(stage.Stage, out var sv))
            {
                foreach (var (section, value) in sv)
                    stageVars[section] = JsonSerializer.SerializeToElement(value, WorkflowVariableJson.Options);
            }

            return stageVars.Count > 0 ? stage with { Variables = stageVars } : stage;
        }).ToList();

        var migrated = definition with { Variables = variables, Stages = stages };
        return new IssueWorkflowProfile(profileId, migrated);
    }

    private static IssueStage LegacyStageName(JsonElement value)
    {
        var stage = LegacyName(value, ["backlog", "plan", "build", "check", "integrate", "done"]);
        return stage switch
        {
            "done" => IssueStage.Done,
            "backlog" => IssueStage.Backlog,
            _ => IssueStage.InProgress,
        };
    }

    private static string LegacyName(JsonElement value, string[] names)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index) && index >= 0 && index < names.Length)
            return names[index];
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString()?.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
            {
                "inprogress" => "in_progress",
                { } name => name,
                _ => names[0],
            };
        return names[0];
    }
}
