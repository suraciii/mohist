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
///   <item><c>{{workflow_run_id}}</c> is read from the envelope's
///         <c>extensions["workflowrunid"]</c>. Empty string when the
///         envelope carries no workflow run id.</item>
///   <item><c>{{stage}}</c> is read from the envelope's
///         <c>extensions["stage"]</c>. Empty string when the envelope
///         carries no stage.</item>
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
        rendered = rendered.Replace(
            WorkflowRunIdToken,
            CloudEventLineage.ReadValue(evt.Extensions, EventCatalog.Lineage.WorkflowRunId) ?? string.Empty,
            StringComparison.Ordinal);
        rendered = rendered.Replace(
            StageToken,
            CloudEventLineage.ReadValue(evt.Extensions, EventCatalog.Lineage.Stage) ?? string.Empty,
            StringComparison.Ordinal);
        rendered = rendered.Replace(EventTypeToken, evt.Type ?? string.Empty, StringComparison.Ordinal);
        return rendered;
    }

}
