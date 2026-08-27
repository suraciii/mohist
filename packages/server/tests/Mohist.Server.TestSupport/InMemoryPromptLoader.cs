using Mohist.Server.Workflow.Domain.Prompts;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.TestSupport;

public sealed class InMemoryPromptLoader : IPromptLoader
{
    private readonly Dictionary<string, SystemTemplate> _templates;

    public InMemoryPromptLoader()
        : this(DefaultTemplates())
    {
    }

    private static IEnumerable<SystemTemplate> DefaultTemplates()
    {
        yield return Template(
            "plan",
            "Plan Change",
            "Creates Workspace plan artifacts for an issue",
            ["plan"],
            "plan");

        foreach (var key in DefaultKeys.Where(key => key != "plan"))
            yield return Template(key, key, string.Empty, [], null);
    }

    private static SystemTemplate Template(
        string key,
        string displayName,
        string description,
        string[] tags,
        string? stage) =>
        new(
            key,
            displayName,
            description,
            tags,
            stage,
            $"Read the current Mohist issue details before handling the {key} artifact.");

    private static readonly string[] DefaultKeys =
    [
        "apply-feedback",
        "build-task",
        "fix-ci",
        "fix-pr-checks",
        "plan",
        "resolve-rebase-conflicts",
        "review",
    ];

    public InMemoryPromptLoader(IEnumerable<SystemTemplate> templates)
    {
        _templates = templates.ToDictionary(template => template.Key, StringComparer.Ordinal);
    }

    public Dictionary<string, string> LoadAll() =>
        _templates.ToDictionary(entry => entry.Key, entry => entry.Value.Body, StringComparer.Ordinal);

    public Dictionary<string, SystemTemplate> LoadAllTemplates() =>
        new(_templates, StringComparer.Ordinal);
}
