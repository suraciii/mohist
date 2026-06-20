using System.Text.Json;

namespace Mohist.Server.Infrastructure.Events;

public sealed class CloudEvent
{
    public string Id { get; }
    public Uri Source { get; }
    public string Type { get; }
    public DateTimeOffset Time { get; }
    public JsonElement? Data { get; }
    public string? DataContentType { get; }
    public string? Subject { get; }
    public string SpecVersion { get; }
    public IReadOnlyDictionary<string, string> Extensions { get; }

    public CloudEvent(
        string id,
        Uri source,
        string type,
        DateTimeOffset time,
        JsonElement? data,
        string? dataContentType = "application/json",
        string? subject = null,
        string specVersion = "1.0",
        IReadOnlyDictionary<string, string>? extensions = null)
    {
        Id = id;
        Source = source;
        Type = type;
        Time = time;
        Data = data;
        DataContentType = dataContentType;
        Subject = subject;
        SpecVersion = specVersion;
        Extensions = extensions ?? new Dictionary<string, string>();
    }

    public static readonly JsonSerializerOptions JsonOptions = JSON.Options;
}

public sealed class CloudEvent<TData> where TData : class
{
    public string Id { get; }
    public Uri Source { get; }
    public string Type { get; }
    public DateTimeOffset Time { get; }
    public TData Data { get; }
    public string? DataContentType { get; }
    public string? Subject { get; }
    public string SpecVersion { get; }
    public IReadOnlyDictionary<string, string> Extensions { get; }

    public CloudEvent(
        string id,
        Uri source,
        string type,
        DateTimeOffset time,
        TData data,
        string? dataContentType = "application/json",
        string? subject = null,
        string specVersion = "1.0",
        IReadOnlyDictionary<string, string>? extensions = null)
    {
        Id = id;
        Source = source;
        Type = type;
        Time = time;
        Data = data;
        DataContentType = dataContentType;
        Subject = subject;
        SpecVersion = specVersion;
        Extensions = extensions ?? new Dictionary<string, string>();
    }
}
