using System.Text.Json;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Classifies failed <c>turn.failed</c> events as <c>context_exhaustion</c>
/// when the final context window usage indicates the agent ran out of room.
///
/// <para>
/// The classifier is a pure function over the event payload and the
/// last known context window metrics. It exists as a separate helper so the
/// rule set can be unit-tested without standing up the agent session grain
/// (the grain only needs to call <see cref="ClassifyTurnFailure(string?, long?, long?, TimeSpan?, bool)"/>
/// on every <c>turn.failed</c> runtime event and rewrite the
/// failureCategory field when the rule matches).
/// </para>
/// </summary>
public static class ContextExhaustionClassifier
{
    public const string ContextExhaustionCategory = "context_exhaustion";
    public const string SuspectedContextExhaustionCategory = "context_exhaustion_suspected";

    /// <summary>
    /// 90% of the context window is the threshold at which a
    /// <c>failed</c> turn is considered context exhaustion. The
    /// boundary is inclusive (≥ 90%) so a turn that fails at 90.0%
    /// is still classified as exhausted — it had no headroom left to
    /// recover. 80-90% is the warning band (handled elsewhere by the
    /// retry guard) and &lt; 90% is treated as a non-exhaustion failure.
    /// </summary>
    public const double ExhaustionRatio = 0.90d;

    /// <summary>
    /// 85% combined with a sub-10s completion is the secondary heuristic
    /// that flags a suspiciously fast failed turn as suspected exhaustion
    /// (no expected output, no useful work).
    /// </summary>
    public const double RapidCompletionUsageRatio = 0.85d;

    /// <summary>
    /// Sessions that complete in under this duration without producing
    /// expected output are flagged as suspected context exhaustion when
    /// usage was already above <see cref="RapidCompletionUsageRatio"/>.
    /// </summary>
    public static readonly TimeSpan RapidCompletionThreshold = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Result of evaluating a failed-turn payload for context
    /// exhaustion. <see cref="Category"/> is <c>null</c> when the event
    /// does not match any exhaustion rule, in which case the
    /// caller should preserve the existing <c>failureCategory</c>
    /// already on the event (probe_timeout, prompt_missing, etc.).
    /// </summary>
    public readonly record struct ClassificationResult(
        string? Category,
        double? ContextUsagePercent,
        bool IsSuspected,
        bool IsExhausted);

    public static ClassificationResult ClassifyTurnFailure(
        string? status,
        long? contextWindowUsed,
        long? contextWindowSize,
        TimeSpan? elapsed,
        bool producedExpectedOutput)
    {
        var percent = AgentSessionJsonHelper.ContextUsagePercent(contextWindowUsed, contextWindowSize);
        return ClassifyTurnFailure(status, percent, elapsed, producedExpectedOutput);
    }

    public static ClassificationResult ClassifyTurnFailure(
        string? status,
        double? contextUsagePercent,
        TimeSpan? elapsed,
        bool producedExpectedOutput)
    {
        if (!string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            // Exhaustion classification is only applied to failed sessions.
            // Successful completions at high usage are healthy (auto-compaction
            // or manual recovery brought usage down before completion).
            return new ClassificationResult(null, contextUsagePercent, false, false);
        }

        if (contextUsagePercent is null)
        {
            return new ClassificationResult(null, contextUsagePercent, false, false);
        }

        if (contextUsagePercent.Value >= ExhaustionRatio * 100d)
        {
            return new ClassificationResult(
                ContextExhaustionCategory,
                contextUsagePercent,
                IsSuspected: false,
                IsExhausted: true);
        }

        return new ClassificationResult(null, contextUsagePercent, false, false);
    }

    /// <summary>
    /// Applies the secondary rapid-completion heuristic. A turn that
    /// fails in under <see cref="RapidCompletionThreshold"/> without
    /// expected output is flagged as suspected context exhaustion when
    /// usage was above <see cref="RapidCompletionUsageRatio"/>.
    ///
    /// <para>
    /// The primary exhausted classifier handles failures at
    /// ≥90% usage; this heuristic covers the rapid completion regime
    /// below that threshold.
    /// </para>
    /// </summary>
    public static ClassificationResult ClassifyRapidCompletion(
        string? status,
        double? contextUsagePercent,
        TimeSpan? elapsed,
        bool producedExpectedOutput)
    {
        if (producedExpectedOutput)
        {
            return new ClassificationResult(null, contextUsagePercent, false, false);
        }

        if (contextUsagePercent is null || elapsed is null)
        {
            return new ClassificationResult(null, contextUsagePercent, false, false);
        }

        if (contextUsagePercent.Value < RapidCompletionUsageRatio * 100d)
        {
            return new ClassificationResult(null, contextUsagePercent, false, false);
        }

        if (elapsed.Value > RapidCompletionThreshold)
        {
            return new ClassificationResult(null, contextUsagePercent, false, false);
        }

        return new ClassificationResult(
            SuspectedContextExhaustionCategory,
            contextUsagePercent,
            IsSuspected: true,
            IsExhausted: false);
    }

    /// <summary>
    /// Rewrites the <c>failureCategory</c> field on a <c>turn.failed</c>
    /// payload when the classifier assigns one. Returns the new payload
    /// JSON (or the original JSON when no rewrite is required).
    /// </summary>
    public static string? ApplyToPayload(string payloadJson, ClassificationResult result)
    {
        if (result.Category is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
                dict[prop.Name] = JsonSerializer.Deserialize<object?>(prop.Value.GetRawText(), JSON.Options);
            dict["failureCategory"] = result.Category;
            if (result.ContextUsagePercent is not null && !dict.ContainsKey("contextUsagePercent"))
                dict["contextUsagePercent"] = result.ContextUsagePercent;
            if (!dict.ContainsKey("contextExhaustion"))
                dict["contextExhaustion"] = result.IsExhausted;
            if (!dict.ContainsKey("contextExhaustionSuspected"))
                dict["contextExhaustionSuspected"] = result.IsSuspected;
            return JSON.Serialize(dict);
        }
        catch
        {
            return null;
        }
    }
}
