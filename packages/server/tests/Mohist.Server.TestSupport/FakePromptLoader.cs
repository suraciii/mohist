using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.TestSupport;

public sealed class FakePromptLoader : IPromptLoader
{
    public Dictionary<string, string> Prompts { get; set; } = new(StringComparer.Ordinal)
    {
        ["plan"] = "# Plan\nCreate PLAN.md, DESIGN.md, and tasks.json",
        ["review"] = "# Review\nRecord review evidence",
        ["build-task"] = "# Build\nImplement task",
        ["apply-feedback"] = "# Feedback\nApply feedback",
        ["fix-ci"] = "# Fix CI\nRepair verification",
        ["fix-pr-checks"] = "# Fix PR checks\nRepair checks",
        ["resolve-rebase-conflicts"] = "# Resolve conflicts\nRepair rebase",
    };

    public string Load(string name) =>
        Prompts.TryGetValue(name, out var value)
            ? value
            : throw new KeyNotFoundException($"Prompt '{name}' not found");

    public Dictionary<string, string> LoadAll() => new(Prompts, StringComparer.Ordinal);
}
