using Mohist.Server.Runner.Grains;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Authoritative capabilities for tests that exercise ordinary dispatch.
/// Production runners provide this evidence through registration; these
/// fixtures opt in explicitly so tests remain independent of the admission
/// policy for missing catalogs.
/// </summary>
public static class CapabilityCatalogTestHelpers
{
    private static readonly string[] Models =
    [
        "openai/gpt-4",
        "openai/gpt-4o",
        "openai/gpt-5.4",
        "openai/gpt-5.5",
        "openai/gpt-5.6",
        "openai/gpt-test",
        "anthropic/sonnet-4.6",
        "anthropic/claude-opus-4-20250514",
        "anthropic/claude-sonnet-4-20250514",
        "anthropic/claude-sonnet-4-6",
        "kimi-for-coding/k2p6",
        "minimax-coding-plan/MiniMax-M3",
        "test/model",
        "gpt-4o",
        "gpt-5",
        "gpt-5.6-luna",
        "claude-3",
        "model-a",
        "model-b",
        "model-test",
        "build-model",
        "project-model",
        "project/default",
        "workflow-wide-model",
        "issue-model",
        "issue-stage-model",
        "stage-model",
        "old-coding/legacy",
    ];

    private static readonly string[] Variants =
    [
        "balanced",
        "fast",
        "high",
        "xhigh",
        "old-issue-variant",
        "old-project-variant",
        "project-stage-variant",
        "project-variant",
        "stage-variant",
    ];

    public static Dictionary<string, RuntimeCatalogEntry> Create()
    {
        var variants = Models.ToDictionary(model => model, _ => Variants, StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, RuntimeCatalogEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["opencode"] = new RuntimeCatalogEntry(
                Models,
                variants,
                SupportsReasoningEffort: true,
                Complete: true,
                CapabilityRevision: "test-opencode-capability-v1"),
            ["pi"] = new RuntimeCatalogEntry(
                Models,
                variants,
                SupportsReasoningEffort: true,
                Complete: true,
                CapabilityRevision: "test-pi-capability-v1"),
        };
    }
}
