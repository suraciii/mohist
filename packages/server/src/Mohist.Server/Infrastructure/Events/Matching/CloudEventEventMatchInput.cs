namespace Mohist.Server.Infrastructure.Events.Matching;

/// <summary>
/// Adapts the canonical <see cref="CloudEvent"/> envelope to the
/// transport-independent <see cref="EventMatchInput"/> view the
/// matcher evaluates against. Resolution rules:
/// <list type="bullet">
///   <item><description><c>type</c>, <c>source</c>, <c>subject</c> resolve to
///         the corresponding <see cref="CloudEvent"/> core fields
///         (<c>Source</c> is rendered as a URI string). A null <c>Subject</c>
///         reads as empty and is treated as absent by <see cref="Has"/>.</description></item>
///   <item><description>Any other attribute name resolves to the
///         <see cref="CloudEvent.Extensions"/> entry of that name;
///         missing entries read as empty and report absent to
///         <see cref="Has"/>.</description></item>
///   <item><description><c>event.data</c> is intentionally unresolvable —
///         the parser rejects it at compile time. This adapter never
///         receives a request for it.</description></item>
/// </list>
/// </summary>
internal sealed class CloudEventEventMatchInput : EventMatchInput
{
    private const string TypeAttribute = "type";
    private const string SourceAttribute = "source";
    private const string SubjectAttribute = "subject";

    private readonly CloudEvent _event;
    private readonly bool _hasSubject;
    private readonly bool _hasExtensions;

    public CloudEventEventMatchInput(CloudEvent cloudEvent)
    {
        ArgumentNullException.ThrowIfNull(cloudEvent);
        _event = cloudEvent;
        _hasSubject = cloudEvent.Subject is not null;
        _hasExtensions = cloudEvent.Extensions.Count > 0;
    }

    public string GetValue(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (attribute == TypeAttribute)
            return _event.Type ?? string.Empty;
        if (attribute == SourceAttribute)
            return _event.Source?.ToString() ?? string.Empty;
        if (attribute == SubjectAttribute)
            return _event.Subject ?? string.Empty;
        if (_hasExtensions && _event.Extensions.TryGetValue(attribute, out var value))
            return value ?? string.Empty;
        return string.Empty;
    }

    public bool Has(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (attribute == TypeAttribute)
            return !string.IsNullOrEmpty(_event.Type);
        if (attribute == SourceAttribute)
            return _event.Source is not null;
        if (attribute == SubjectAttribute)
            return _hasSubject;
        return _hasExtensions && _event.Extensions.ContainsKey(attribute);
    }
}