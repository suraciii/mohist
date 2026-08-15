namespace Mohist.Server.Agent.Services;

public static class BuiltInAgentCatalog
{
    public const string MohistSlackName = "mohist-slack";
    public const string MohistSlackProjectId = "__mohist_slack_manager__";

    public static IReadOnlyList<BuiltInAgentDefinition> Definitions { get; } =
    [
        new(
            MohistSlackName,
            "Mohist's Slack workspace manager.",
            BuiltInAgentAssets.MohistSlackInstructions,
            Runtime: "opencode",
            Model: null,
            Variant: null,
            Skills: [])
    ];

    public static bool IsReservedName(string? name) =>
        string.Equals(name?.Trim(), MohistSlackName, StringComparison.OrdinalIgnoreCase);

    public static BuiltInAgentDefinition? Find(string? name) =>
        Definitions.FirstOrDefault(definition =>
            string.Equals(definition.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static AgentInfo Resolve(string name)
    {
        var definition = Find(name)
            ?? throw new KeyNotFoundException($"Built-in Agent '{name}' was not found.");
        return new AgentInfo(
            Id: $"builtin:{definition.Name}",
            ProjectId: MohistSlackProjectId,
            Name: definition.Name,
            Description: definition.Description,
            Instructions: definition.Instructions,
            AgentConfig: AgentConfig(definition),
            Skills: definition.Skills,
            MaxConcurrentRuns: null,
            Status: Domain.AgentStatus.Active,
            CreatedAt: string.Empty,
            UpdatedAt: string.Empty,
            Permissions: []);
    }

    private static System.Text.Json.JsonElement AgentConfig(BuiltInAgentDefinition definition)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runtime"] = definition.Runtime,
        };
        if (!string.IsNullOrWhiteSpace(definition.Model))
            values["model"] = definition.Model;
        if (!string.IsNullOrWhiteSpace(definition.Variant))
            values["variant"] = definition.Variant;
        return System.Text.Json.JsonSerializer.SerializeToElement(values);
    }
}

public sealed record BuiltInAgentDefinition(
    string Name,
    string Description,
    string Instructions,
    string Runtime,
    string? Model,
    string? Variant,
    IReadOnlyList<string> Skills);

internal static class BuiltInAgentAssets
{
    private const string AssetSuffix = ".Agent.Services.Assets.mohist-slack.instructions.md";

    public static string MohistSlackInstructions { get; } = Read(AssetSuffix);

    private static string Read(string suffix)
    {
        var assembly = typeof(BuiltInAgentAssets).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException($"Embedded Agent asset '{suffix}' was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Agent asset '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
