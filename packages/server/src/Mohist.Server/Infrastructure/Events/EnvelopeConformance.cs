namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Conformance check for stamped event envelopes. Asserts an emitted envelope
/// satisfies the lineage attributes its <see cref="EventCatalog"/> entry declares
/// as required. Declaration is the single source of truth producers (T-002..T-006)
/// stamp against and that the distributed conformance assertions call.
/// </summary>
public static class EnvelopeConformance
{
    /// <summary>
    /// The attributes that <paramref name="envelope"/> is missing relative to the
    /// required-attribute declaration for <paramref name="envelope"/>'s type. When
    /// the type is not registered in the lineage registry, returns an empty list
    /// — those types are out of protocol scope (transcript / legacy names).
    /// </summary>
    public static IReadOnlyList<string> Missing(IReadOnlyDictionary<string, string> extensions, string type)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(type);

        var required = EventCatalog.RequiredAttributes(type);
        if (required.Count == 0)
        {
            return [];
        }

        var missing = new List<string>(required.Count);
        foreach (var attribute in required)
        {
            if (!extensions.TryGetValue(attribute, out var value) || string.IsNullOrEmpty(value))
            {
                missing.Add(attribute);
            }
        }

        return missing;
    }

    /// <summary>
    /// The attributes that <paramref name="envelope"/> is missing relative to the
    /// required-attribute declaration for its type. Convenience overload.
    /// </summary>
    public static IReadOnlyList<string> Missing(CloudEvent envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Missing(envelope.Extensions, envelope.Type);
    }

    /// <summary>
    /// Throws <see cref="EnvelopeConformanceException"/> when the envelope is
    /// missing any required attribute for its type. When the type is not
    /// registered in the lineage registry the call is a no-op (those types are
    /// out of protocol scope).
    /// </summary>
    public static void AssertRequired(CloudEvent envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var missing = Missing(envelope);
        if (missing.Count > 0)
        {
            throw new EnvelopeConformanceException(envelope.Type, missing);
        }
    }

    /// <summary>
    /// Throws when the <paramref name="extensions"/> for a given <paramref name="type"/>
    /// are missing any required attribute. Useful when the assertion runs against an
    /// extensions dictionary rather than a constructed <see cref="CloudEvent"/>.
    /// </summary>
    public static void AssertRequired(IReadOnlyDictionary<string, string> extensions, string type)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(type);
        var missing = Missing(extensions, type);
        if (missing.Count > 0)
        {
            throw new EnvelopeConformanceException(type, missing);
        }
    }
}

public sealed class EnvelopeConformanceException : Exception
{
    public string EventType { get; }
    public IReadOnlyList<string> MissingAttributes { get; }

    public EnvelopeConformanceException(string eventType, IReadOnlyList<string> missingAttributes)
        : base($"Event envelope of type '{eventType}' is missing required lineage attribute(s): {string.Join(", ", missingAttributes)}.")
    {
        EventType = eventType;
        MissingAttributes = missingAttributes;
    }
}
