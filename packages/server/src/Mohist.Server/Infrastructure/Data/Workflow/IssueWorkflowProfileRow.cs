namespace Mohist.Server.Infrastructure.Data.Workflow;

/// <summary>
/// Issue workflow profile.
/// 存储 issue 级模板引用/自定义模板 + issue 级变量配置。
/// </summary>
public class IssueWorkflowProfile
{
    public string ProjectId { get; set; } = string.Empty;
    public int IssueNumber { get; set; }

    /// <summary>
    /// 引用的项目模板 ID (当用户为 issue 选择了特定项目模板时)。
    /// 为 null 表示继承项目默认模板。
    /// </summary>
    public string? SourceTemplateId { get; set; }

    /// <summary>
    /// WorkflowDefinition JSON - issue 级自定义模板结构。
    /// 当用户通过 UpdateTemplateAsync 传了 Template 参数时设置。
    /// 不为 null 时优先使用此结构, SourceTemplateId 被忽略。
    /// </summary>
    public string? Template { get; set; }

    /// <summary>
    /// VariableBundle JSON - issue 级变量配置。
    /// </summary>
    public string Variables { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
