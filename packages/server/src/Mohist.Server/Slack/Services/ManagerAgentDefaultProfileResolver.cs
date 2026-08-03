using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Slack.Services;

public sealed class ManagerAgentDefaultProfileResolver : IScopedService
{
    public ManagerAgentDefaultProfile Resolve() =>
        new(AgentConfigSchema.OpenCodeRuntime, null, null);
}

public sealed record ManagerAgentDefaultProfile(
    string Runtime,
    string? Model,
    string? Variant)
{
    public JsonElement ToAgentConfig()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runtime"] = Runtime,
        };
        if (!string.IsNullOrWhiteSpace(Model))
            values["model"] = Model;
        if (!string.IsNullOrWhiteSpace(Variant))
            values["variant"] = Variant;
        return JsonSerializer.SerializeToElement(values);
    }
}
