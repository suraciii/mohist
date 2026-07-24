using System.Text.Json;

namespace Mohist.Cli;

internal sealed class PresetCatalog
{
    private readonly IFileSystem _fileSystem;
    private readonly string? _assetRoot;

    // Production entry point: resolves the preset asset root independently of
    // skill-data (design D2). Tests build the catalog against an explicit root
    // via the (IFileSystem, string) constructor, or against the real resolution
    // path via CreateDefault with a fake file system + home.
    public static PresetCatalog CreateDefault(IFileSystem fileSystem, Func<string?> getUserHome) =>
        new(fileSystem, new PresetAssetRootResolver(fileSystem, getUserHome).Resolve());

    internal PresetCatalog(IFileSystem fileSystem, string? assetRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _assetRoot = assetRoot;
    }

    public IReadOnlyList<string> ListNames()
    {
        var manifest = ReadManifest();
        return manifest?.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
    }

    public PresetCatalogResult Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return PresetCatalogResult.NotFound(name, ListNames());

        var manifest = ReadManifest();
        if (manifest is null || !manifest.TryGetValue(name, out var definition))
            return PresetCatalogResult.NotFound(name, manifest?.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>());

        try
        {
            var instructions = ReadAsset(definition.Instructions);
            var rules = definition.Rules.Select(rule => new PresetRule(
                rule.Name,
                rule.Match,
                ReadAsset(rule.ResponsePrompt))).ToArray();
            return PresetCatalogResult.Success(new AgentPreset(name, instructions, rules));
        }
        catch (Exception exception) when (exception is IOException or FileNotFoundException or JsonException)
        {
            return PresetCatalogResult.Failed(exception.Message);
        }
    }

    private Dictionary<string, PresetManifestEntry>? ReadManifest()
    {
        if (_assetRoot is null)
            return null;

        var path = Path.Combine(_assetRoot, "manifest.json");
        if (!_fileSystem.Exists(path))
            return null;

        var json = _fileSystem.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, PresetManifestEntry>(StringComparer.Ordinal);
        foreach (var preset in document.RootElement.EnumerateObject())
        {
            var definition = preset.Value;
            var instructions = GetValue(definition, "instructions") ?? throw new JsonException("Preset instructions are required.");
            var rules = definition.GetProperty("rules").EnumerateArray().Select(rule => new PresetManifestRule(
                GetValue(rule, "name") ?? throw new JsonException("Preset rule name is required."),
                GetValue(rule, "match") ?? throw new JsonException("Preset rule match is required."),
                GetValue(rule, "responsePrompt") ?? throw new JsonException("Preset rule response prompt is required."))).ToArray();
            result[preset.Name] = new PresetManifestEntry(instructions, rules);
        }

        return result;
    }

    private string ReadAsset(string relativePath)
    {
        if (_assetRoot is null)
            throw new FileNotFoundException("Preset asset root could not be resolved.");

        return _fileSystem.ReadAllText(Path.Combine(_assetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string? GetValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private sealed record PresetManifestEntry(string Instructions, PresetManifestRule[] Rules);
    private sealed record PresetManifestRule(string Name, string Match, string ResponsePrompt);
}

internal sealed record AgentPreset(string Name, string Instructions, IReadOnlyList<PresetRule> Rules);

internal sealed record PresetRule(string Name, string Match, string ResponsePrompt);

internal sealed record PresetCatalogResult(bool Found, string? Error, IReadOnlyList<string> AvailableNames, AgentPreset? Preset)
{
    public static PresetCatalogResult Success(AgentPreset preset) => new(true, null, Array.Empty<string>(), preset);

    public static PresetCatalogResult NotFound(string? name, IReadOnlyList<string> availableNames) =>
        new(false, $"Unknown preset '{name}'. Available presets: {string.Join(", ", availableNames)}.", availableNames, null);

    public static PresetCatalogResult Failed(string error) => new(false, error, Array.Empty<string>(), null);
}
