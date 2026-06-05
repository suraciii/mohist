using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Infrastructure.Serialization;

public static class WorkflowVariableJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
