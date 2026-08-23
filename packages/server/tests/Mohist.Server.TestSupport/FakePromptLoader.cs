using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.TestSupport;

public sealed class FakePromptLoader : IPromptLoader
{
    public Dictionary<string, string> Prompts { get; set; } = new(StringComparer.Ordinal)
    {
        ["proposal"] = "# Proposal Artifact\nCreate proposal.md",
        ["specs"] = "# Specs Artifact\nCreate specs",
        ["design"] = "# Design Artifact\nCreate design.md",
        ["tasks"] = "# Tasks Artifact\nCreate tasks.json",
        ["self-review"] = "# Self Review\nReview artifacts",
        ["review"] = "# Review\nReview implementation",
        ["build"] = "# Build\nImplement task",
    };

    public string Load(string name) =>
        Prompts.TryGetValue(name, out var value)
            ? value
            : throw new KeyNotFoundException($"Prompt '{name}' not found");

    public Dictionary<string, string> LoadAll() => new(Prompts, StringComparer.Ordinal);
}
