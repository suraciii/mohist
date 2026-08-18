using System.Text.Json;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Retained pre-change aggregate definitions for the affected built-in
/// profiles, kept so that legacy runs (no <c>BoundWorkflowDefinitionJson</c>)
/// continue to materialize their original aggregate <c>verify</c> task after
/// the built-in profiles have been updated to six ordered lanes. The
/// aggregate task declares a literal positive finite <c>core/script</c>
/// timeout and the same profile-specific <c>fix-ci</c> recovery contract
/// every lane now carries, so the legacy path remains the same observable
/// behavior the runs were originally bound to.
///
/// Pre-snapshot runs MUST never be made to wait for synthesized lane state;
/// the legacy path resolves only the build stage's aggregate task from here.
/// </summary>
public static class RetainedLegacyAggregate
{
    public const string LocalProfileId = "mohist/local";
    public const string GitHubPrProfileId = "mohist/github-pr";

    private const int LegacyVerifyTimeoutMs = 300000;
    private const int FixCiRecoveryBudget = 2;

    private static readonly Lazy<WorkflowDefinition> _localLegacy = new(BuildLocalLegacy);
    private static readonly Lazy<WorkflowDefinition> _githubPrLegacy = new(BuildGitHubPrLegacy);

    public static WorkflowDefinition? TryGetLegacyDefinition(string? profileId)
    {
        if (string.Equals(profileId, LocalProfileId, StringComparison.Ordinal))
            return _localLegacy.Value;
        if (string.Equals(profileId, GitHubPrProfileId, StringComparison.Ordinal))
            return _githubPrLegacy.Value;
        return null;
    }

    /// <summary>
    /// Resolves a single stage from the retained legacy definition for a
    /// profile, or returns <c>null</c> when the profile has no retained
    /// legacy definition (e.g. custom profiles, runs bound after this code
    /// was deployed which carry their own snapshot).
    /// </summary>
    public static StageDefinition? TryGetLegacyDefinition(string? profileId, string stageId)
    {
        var legacy = TryGetLegacyDefinition(profileId);
        if (legacy is null) return null;
        return legacy.Stages.FirstOrDefault(s => string.Equals(s.Stage, stageId, StringComparison.Ordinal));
    }

    private static WorkflowDefinition BuildLocalLegacy() =>
        BuildLegacyBuildStage(VerifyAggregateTask(LegacyVerifyTimeoutMs));

    private static WorkflowDefinition BuildGitHubPrLegacy() =>
        BuildLegacyBuildStage(VerifyAggregateTask(LegacyVerifyTimeoutMs));

    /// <summary>
    /// The aggregate <c>verify</c> task exactly as it was declared in the
    /// pre-change built-in profiles: <c>core/script</c> with the
    /// <c>${{ vars.ci.verify }}</c> command and a literal 300000 ms timeout,
    /// plus the existing profile-specific <c>fix-ci</c> recovery declaration
    /// (budget 2, unconditional handler, retrySelf true).
    /// </summary>
    private static TaskDefinition VerifyAggregateTask(int timeoutMs)
    {
        var with = new Dictionary<string, JsonElement?>
        {
            ["run"] = CloneElement("${{ vars.ci.verify }}"),
            ["timeout"] = CloneElement(timeoutMs),
        };

        var fixCiTask = new TaskDefinition(
            Id: "recover:fix-ci",
            Title: "Fix CI verification",
            Uses: "mohist/opencode",
            With: new Dictionary<string, JsonElement?>
            {
                ["session"] = CloneElement("build"),
                ["prompt"] = CloneElement("${{ prompts.fix-ci }}"),
                ["options"] = CloneElement("${{ vars.agent }}"),
            },
            Expect: new Dictionary<string, JsonElement?>
            {
                ["markers"] = CloneElementArray(
                    """
                    [{"path": "_output", "oneOf": ["<promise>done</promise>", "<promise>unfinished</promise>"]}]
                    """),
            });

        var recovery = new RecoveryDefinition(
            Budget: FixCiRecoveryBudget,
            Handlers: new[]
            {
                new RecoveryHandlerDefinition(
                    When: null,
                    Tasks: new[] { fixCiTask },
                    RetrySelf: true),
            });

        return new TaskDefinition(
            Id: "verify",
            Title: "Build & full test suite",
            Uses: "core/script",
            With: with,
            Recovery: recovery);
    }

    private static WorkflowDefinition BuildLegacyBuildStage(TaskDefinition verifyTask) =>
        new(new[]
        {
            new StageDefinition(
                Stage: "build",
                Tasks: new[] { verifyTask },
                Checks: Array.Empty<CheckDefinition>(),
                RequiresApproval: false),
        });

    private static JsonElement CloneElement(string jsonValue) =>
        JsonDocument.Parse(JsonSerializer.Serialize(jsonValue)).RootElement.Clone();

    private static JsonElement CloneElement(int value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static JsonElement CloneElementArray(string jsonArray) =>
        JsonDocument.Parse(jsonArray).RootElement.Clone();
}