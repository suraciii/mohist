namespace Mohist.Server.Infrastructure.Data.Workflow;

public class ProjectWorkflowProfile
{
    public string ProjectId { get; set; } = string.Empty;

    public string? DefaultTemplateId { get; set; }

    /// <summary>
    /// required Project default WorkflowProfile ID. Initial
    /// values are seeded by the data migration; the unique index on
    /// (ProjectId, ProfileId) on the new WorkflowProfileRecord table is the
    /// existence check at write time. Built-in <c>mohist/local</c> is the
    /// initial default for any newly created Project.
    /// </summary>
    public string? DefaultWorkflowProfileId { get; set; }

    /// <summary>
    /// nullable custom-Profile backing key used by the
    /// restrictive foreign key that protects Project default deletions.
    /// Populated only when DefaultWorkflowProfileId resolves to a custom
    /// (non-built-in) Profile in this Project; null when the default is a
    /// built-in, or when the row only holds variables/prompts.
    /// </summary>
    public string? DefaultWorkflowProfileIdKey { get; set; }

    public string Variables { get; set; } = "{}";

    public Dictionary<string, string> Prompts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 是否禁用了内置 issue 模板。
    /// </summary>
    public bool DisableDefaultIssueTemplate { get; set; }

    public List<string> DisabledWorkflowProfileIds { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
