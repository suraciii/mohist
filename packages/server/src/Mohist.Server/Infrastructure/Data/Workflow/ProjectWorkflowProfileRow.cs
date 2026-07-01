namespace Mohist.Server.Infrastructure.Data.Workflow;

public class ProjectWorkflowProfile
{
    public string ProjectId { get; set; } = string.Empty;

    public string? DefaultTemplateId { get; set; }

    public string Variables { get; set; } = "{}";

    public Dictionary<string, string> Prompts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 是否禁用了内置 issue 模板。
    /// </summary>
    public bool DisableDefaultIssueTemplate { get; set; }

    public List<string> DisabledWorkflowProfileIds { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
