using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed class AgentInstallPreflight
{
    private readonly IFileSystem _fileSystem;
    private readonly Func<string> _configPathProvider;

    internal AgentInstallPreflight(IFileSystem fileSystem, Func<string> configPathProvider)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _configPathProvider = configPathProvider ?? throw new ArgumentNullException(nameof(configPathProvider));
    }

    // The project API exposes no per-repository workspace path (control/execution
    // plane separation: the runner checks repos out on its own host). The CLI can
    // only inspect the directory the user invoked `mo` from as a *local proxy*
    // for where the supervisor will actually run. `localProxyPath` is therefore
    // the CLI's current directory, and the warning is framed as a best-effort
    // signal, not a definitive statement about the runner workspace.
    public PreflightResult Run(string localProxyPath, DefaultRepository defaultRepository)
    {
        ArgumentNullException.ThrowIfNull(localProxyPath);

        var warnings = new List<string>();
        var notices = new List<string>();

        if (!defaultRepository.Resolved)
        {
            notices.Add("note: project has no default repository; skipping skill stub check");
        }
        else
        {
            var skillStubPath = Path.Combine(localProxyPath, ".agents", "skills", "mohist");
            if (!_fileSystem.DirectoryExists(skillStubPath))
            {
                var repo = string.IsNullOrWhiteSpace(defaultRepository.Name)
                    ? "the default repository"
                    : $"default repository '{defaultRepository.Name}'";
                warnings.Add(
                    $"warning: could not find the 'mohist' skill stub in the current directory '{localProxyPath}', " +
                    $"which is being used as a local proxy for {repo}'s workspace (the CLI cannot inspect the runner's " +
                    $"actual checkout). If the supervisor runs from a workspace without the stub, it will not discover " +
                    $"the mo command surface. Repair in the repository checkout: run `mo skills install --path {localProxyPath}`.");
            }
        }

        var notificationWarning = CheckNotifications();
        if (notificationWarning is not null)
            warnings.Add(notificationWarning);

        return new PreflightResult(warnings, notices);
    }

    private string? CheckNotifications()
    {
        var configPath = _configPathProvider();
        if (string.IsNullOrWhiteSpace(configPath) || !_fileSystem.Exists(configPath))
            return null;

        JsonObject? root;
        try
        {
            var text = _fileSystem.ReadAllText(configPath);
            root = JsonNode.Parse(NotifyCommands.StripJsoncComments(text)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var hermes = root?["Mohist"]?["Notifications"]?["Hermes"];
        if (hermes is null)
            return null;

        var enabledTypes = (hermes as JsonObject)?["EnabledTypes"] as JsonArray;
        if (enabledTypes is null)
            return null;

        var enabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in enabledTypes)
        {
            var item = value?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(item))
                enabled.Add(item);
        }

        var missing = new List<string>();
        foreach (var required in NotifyCommands.DefaultEnabledTypes)
        {
            if (!enabled.Contains(required))
                missing.Add(required);
        }

        if (missing.Count == 0)
            return null;

        return
            $"warning: Mohist:Notifications:Hermes:EnabledTypes is missing " +
            $"{string.Join(", ", missing)}. With notifications disabled, the owner can only " +
            "discover a stopped or failed supervisor by actively checking, not by being notified.";
    }
}

internal readonly record struct DefaultRepository(bool Resolved, string? Name)
{
    public static readonly DefaultRepository Unresolved = new(false, null);
    public static DefaultRepository Named(string? name) => new(true, name);
}

internal sealed record PreflightResult(IReadOnlyList<string> Warnings, IReadOnlyList<string> Notices);
