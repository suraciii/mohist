using System.Text.Json;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Infrastructure.Serialization;

public static class WorkflowVariableJson
{
    public static readonly JsonSerializerOptions Options = JSON.Options;
}
