using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public static class WorkflowStorageJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

public static class WorkflowLeaseJson
{
    public static WorkLease? Deserialize(string json) =>
        json == "null" ? null : JsonSerializer.Deserialize<WorkLease>(json, WorkflowStorageJson.Options);

    public static string Serialize(WorkLease state) =>
        JsonSerializer.Serialize(state, WorkflowStorageJson.Options);
}
