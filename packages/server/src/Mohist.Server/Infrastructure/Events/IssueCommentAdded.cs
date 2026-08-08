using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// CloudEvents 1.0.2 payload for <see cref="EventCatalog.ReverseDns.IssueCommentAdded"/>.
/// Standalone record — comments are a side record on the issue aggregate, not a
/// state transition, so the event bypasses the <c>IssueEvent</c> union /
/// <c>IssueEventSerializer</c> surface. <see cref="IssueCommentAddedEventFactory"/>
/// builds the envelope and stamps it with <see cref="IssueLineage.BuildExtensions"/>;
/// <see cref="IssueGrain.AddCommentAsync"/> emits it inside the same transaction as
/// the comment row so a subscriber that observes the event always finds the row.
/// </summary>
public sealed record IssueCommentAdded(
    string CommentId,
    string Author,
    string? DisplayName,
    string Body);

internal static class IssueCommentAddedEventFactory
{
    private const string SpecVersion = "1.0";

    private static readonly JsonSerializerOptions JsonOptions = JSON.Options;

    internal static JsonElement ToData(IssueCommentAdded payload) =>
        JsonSerializer.SerializeToElement(payload, JsonOptions);

    /// <summary>
    /// Build the CloudEvent envelope for a freshly-persisted comment. The
    /// envelope carries the reverse-DNS <c>type</c>, the issue-scoped
    /// <c>source</c>, the issue number as <c>subject</c>, the lineage
    /// extensions (<c>projectid</c>, <c>issue</c>, and optionally
    /// <c>epic</c>/<c>parent</c> when the producing issue carries them),
    /// and a JSON-serialized payload.
    /// </summary>
    internal static CloudEvent Build(
        DomainIssue owner,
        IssueCommentAdded payload,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(payload);

        var source = IssueEventPersistence.IssueSource(owner.ProjectId, owner.Number);
        var extensions = IssueLineage.BuildExtensions(owner);
        var data = ToData(payload);

        return new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCommentAdded,
            time: now,
            data: data,
            subject: owner.Number.ToString(),
            specVersion: SpecVersion,
            extensions: extensions);
    }
}
