namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Workflow run profile (key: WorkflowRunId, 1:1 与 workflow run)。
/// 存储 workflow run 级模板快照 + run 级变量配置。
/// </summary>
public class WorkflowProfileRow
{
    public string WorkflowRunId { get; set; } = string.Empty;

    /// <summary>
    /// 关联的 project ID (查询用)。
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// 关联的 issue key (projectId:issueNumber, 查询用)。
    /// </summary>
    public string IssueKey { get; set; } = string.Empty;

    /// <summary>
    /// WorkflowDefinition JSON - workflow run 级模板快照。
    /// workflow 启动时冻结, 运行期不可变。
    /// </summary>
    public string TemplateJson { get; set; } = "{}";

    /// <summary>
    /// VariableBundle JSON - workflow run 级变量 (Layer 5, 最内层)。
    /// 运行期 agent 可 patch。
    /// </summary>
    public string VariablesJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
