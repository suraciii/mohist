using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Agent.Domain;

/// <summary>
/// CloudEvent envelope matcher for the subscription Filter model. Resolves
/// whether a subscription's persisted <see cref="SubscriptionFilter"/>
/// (Type + optional Source + optional Subject) matches an inbound
/// <see cref="CloudEvent"/>.
/// </summary>
/// <remarks>
/// <para>
/// Implemented as a <c>partial</c> of the domain
/// <see cref="SubscriptionFilter"/> class declared in
/// <c>Agent/Domain/AgentSubscription.cs</c> so the dispatch pipeline
/// reads <c>subscription.Filter.Matches(evt)</c> naturally. Field
/// declarations live next to the <see cref="AgentSubscription"/>
/// aggregate; this file owns the matching algorithm only.
/// </para>
/// <para>
/// The Type field reuses the shared <see cref="CloudEventTypeMatcher"/>
/// algorithm (exact / <c>|</c> / <c>*</c> / <c>prefix.*</c>) so the
/// subscription semantics are consistent with the bus-level
/// <c>[Subscription]</c> dispatch. Source / Subject apply Ordinal
/// <see cref="string.Equals(string, StringComparison)"/>: null or
/// whitespace means "no constraint" (per design D2).
/// </para>
/// <para>
/// The matcher is envelope-only — it does not consult Workflow or Issue
/// domain state (spec
/// <c>agent-subscription-dispatch#Subscription dispatch consumes only the
/// CloudEvent envelope</c>).
/// </para>
/// </remarks>
public sealed partial class SubscriptionFilter
{
    /// <summary>
    /// Returns <c>true</c> when the inbound <paramref name="evt"/> matches
    /// this subscription filter:
    /// <list type="bullet">
    ///   <item>Type — exact / <c>|</c>-separated / <c>*</c> / <c>prefix.*</c>.</item>
    ///   <item>Source — Ordinal exact when set, no constraint when null or
    ///         whitespace.</item>
    ///   <item>Subject — Ordinal exact when set, no constraint when null or
    ///         whitespace.</item>
    /// </list>
    /// </summary>
    public bool Matches(CloudEvent? evt)
    {
        if (evt is null) return false;

        if (!CloudEventTypeMatcher.Matches(this.Type, evt.Type))
            return false;

        if (!string.IsNullOrWhiteSpace(this.Source)
            && !string.Equals(this.Source, evt.Source?.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(this.Subject)
            && !string.Equals(this.Subject, evt.Subject, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}