using System.Text.Json;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Plain-text substitution of CloudEvent envelope-sourced placeholders in
/// a subscription's <c>ResponsePrompt</c>. Used by
/// <see cref="AgentSubscriptionDispatchHandler"/> to compose the
/// second-layer prompt fed into <see cref="Mohist.Server.Agent.Services.IAgentLauncher"/>.
/// </summary>
/// <remarks>
/// <para>
/// Supported placeholders:
/// <list type="bullet">
///   <item><c>{{workflow_run_id}}</c> — parsed from the CloudEvent
///         <c>source</c> URI
///         (<c>/mohist/workflow-runs/{runId}</c>) via the shared
///         <see cref="WorkflowStageLockReleaseHandler.ExtractWorkflowRunId"/>
///         helper. Empty string when the source is not a workflow-run
///         URI.</item>
///   <item><c>{{stage}}</c> — read from the envelope's <c>data.stage</c>
///         property (case-insensitive: <c>stage</c> or <c>Stage</c>),
///         unwrapping the WorkflowEvent <c>{"value": {...}}</c> envelope
///         when present. Empty string when the envelope carries no
///         stage.</item>
///   <item><c>{{event_type}}</c> — the CloudEvent <c>type</c>. Empty
///         string when the envelope type is null/empty.</item>
/// </list>
/// </para>
/// <para>
/// Per spec <c>agent-subscription-dispatch#Response prompt is rendered
/// from envelope-carried variables</c> the system SHALL NOT introduce a
/// template engine and SHALL NOT provide an <c>{{issue}}</c> variable —
/// the Agent obtains issue identity itself by running the workflow
/// read command.
/// </para>
/// <para>
/// Unmatched / unrecognized placeholders are left verbatim in the output
/// (per spec <c>Response prompt is rendered from envelope-carried
/// variables#Unsubstituted placeholders left as-is when no envelope
/// value</c>). This keeps the surface deterministic and observable —
/// misconfigured variable names show up in the prompt text instead of
/// silently disappearing.
/// </para>
/// </remarks>
public static class ResponsePromptRenderer
{
    public const string WorkflowRunIdToken = "{{workflow_run_id}}";
    public const string StageToken = "{{stage}}";
    public const string EventTypeToken = "{{event_type}}";

    /// <summary>
    /// Returns a rendered copy of <paramref name="template"/> with the
    /// supported envelope placeholders substituted. Returns the input
    /// template unchanged when it is null/empty.
    /// </summary>
    public static string Render(string? template, CloudEvent? evt)
    {
        if (string.IsNullOrEmpty(template) || evt is null)
            return template ?? string.Empty;

        var rendered = template;
        rendered = rendered.Replace(WorkflowRunIdToken, ExtractWorkflowRunId(evt), StringComparison.Ordinal);
        rendered = rendered.Replace(StageToken, ExtractStage(evt.Data) ?? string.Empty, StringComparison.Ordinal);
        rendered = rendered.Replace(EventTypeToken, evt.Type ?? string.Empty, StringComparison.Ordinal);
        return rendered;
    }

    private static string ExtractWorkflowRunId(CloudEvent evt)
    {
        var source = evt.Source?.ToString();
        if (string.IsNullOrEmpty(source))
            return string.Empty;
        return WorkflowStageLockReleaseHandler.ExtractWorkflowRunId(source);
    }

    /// <summary>
    /// Mirrors <see cref="WorkflowStageLockReleaseHandler.ExtractStage"/>
    /// without coupling to its internal accessibility — kept verbatim so
    /// the envelope-unwrap semantics match the existing stage lock release
    /// handler.
    /// </summary>
    private static string? ExtractStage(JsonElement? data)
    {
        if (data is null || !data.HasValue) return null;
        var value = data.Value;
        if (value.ValueKind != JsonValueKind.Object) return null;

        JsonElement inner = value;
        if (value.TryGetProperty("value", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.Object)
        {
            inner = wrapped;
        }

        if (inner.TryGetProperty("stage", out var lower) && lower.ValueKind == JsonValueKind.String)
            return lower.GetString();
        if (inner.TryGetProperty("Stage", out var upper) && upper.ValueKind == JsonValueKind.String)
            return upper.GetString();
        return null;
    }
}