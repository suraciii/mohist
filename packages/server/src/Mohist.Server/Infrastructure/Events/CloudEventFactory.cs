using CloudNative.CloudEvents;
using System.Text.Json;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Constructs CloudEvents 1.0.2 envelopes with the standard extension attributes
/// (projectid, workflowrunid, issueno) used across mohist for routing and filtering.
/// </summary>
public static class CloudEventFactory
{
    public const string SpecVersion = "1.0";

    public static CloudEvent Create(
        string type,
        Uri source,
        object? data = null,
        string? subject = null,
        string? projectId = null,
        string? workflowRunId = null,
        string? issueNumber = null,
        IReadOnlyDictionary<string, object?>? extraExtensions = null)
    {
        // For back-compat: when the data implements IProjectScoped and the caller
        // did not pass an explicit projectId, lift the ProjectId from the payload
        // into the canonical `projectid` extension. This keeps the legacy emit
        // path (raw payload, no envelope) producing the same routing key as the
        // new envelope-first path.
        if (projectId is null && data is IProjectScoped scoped && !string.IsNullOrEmpty(scoped.ProjectId))
        {
            projectId = scoped.ProjectId;
        }

        var evt = new CloudEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = source,
            Type = type,
            Time = DateTimeOffset.UtcNow,
            Subject = subject,
            DataContentType = "application/json",
        };

        if (data is not null)
        {
            evt.Data = JsonSerializer.SerializeToElement(data, JsonOptions);
        }

        if (projectId is not null) evt["projectid"] = projectId;
        if (workflowRunId is not null) evt["workflowrunid"] = workflowRunId;
        if (issueNumber is not null) evt["issueno"] = issueNumber;

        if (extraExtensions is not null)
        {
            foreach (var (k, v) in extraExtensions)
            {
                evt[k] = v;
            }
        }

        return evt;
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

