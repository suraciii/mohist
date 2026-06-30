using System.Text.Json;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Workflow.Domain;

public sealed class WorkflowControlRejectionException : Exception
{
    public string Code { get; }

    public string? Details { get; }

    public WorkflowControlRejectionException(string code, string message, object? details = null)
        : base(message)
    {
        Code = code;
        Details = details is null ? null : JsonSerializer.Serialize(details, JSON.Options);
    }

    public JsonElement? DetailsJson() =>
        Details is null ? null : JsonSerializer.Deserialize<JsonElement>(Details, JSON.Options);
}
