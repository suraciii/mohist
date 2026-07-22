namespace Mohist.Workflow.Definition;

public static class TemplateRoots
{
    public const string Workflow = "workflow";
    public const string Stage = "stage";
    public const string Work = "work";
    public const string Issue = "issue";
    public const string Repository = "repository";
    public const string Workspace = "workspace";
    public const string Vars = "vars";
    public const string Tasks = "tasks";
    public const string Prompts = "prompts";
    public const string Failure = "failure";

    public static readonly IReadOnlyCollection<string> All = new[]
    {
        Workflow,
        Stage,
        Work,
        Issue,
        Repository,
        Workspace,
        Vars,
        Tasks,
        Prompts,
        Failure,
    };

    public static readonly IReadOnlySet<string> AllowedSet = new HashSet<string>(All, StringComparer.Ordinal);

    public static bool IsAllowed(string root) => AllowedSet.Contains(root);
}