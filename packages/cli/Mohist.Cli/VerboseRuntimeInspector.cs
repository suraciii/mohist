using System.Runtime.InteropServices;

namespace Mohist.Cli;

internal sealed class VerboseRuntimeInspector
{
    private static readonly TimeSpan CollectorTimeout = TimeSpan.FromSeconds(2);

    private readonly ICommandExecutor _commandExecutor;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly MohistCliApi _api;

    public VerboseRuntimeInspector(ICommandExecutor commandExecutor, IEnvironmentVariableProvider environment, MohistCliApi api)
    {
        _commandExecutor = commandExecutor;
        _environment = environment;
        _api = api;
    }

    internal async Task<InfoVerboseOpencodeRuntime> GetOpencodeRuntimeVerboseAsync()
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        try
        {
            var rawCommand = _environment.GetEnvironmentVariable("MOHIST_AGENT_COMMAND") ?? "opencode";
            var (resolvedCommand, commandAllowed) = ValidateAgentCommand(rawCommand);
            string? version = null;
            if (commandAllowed)
            {
                try
                {
                    var (exit, stdout, _) = await InfoCollector.WithTimeout(
                        _commandExecutor.ExecuteAsync(resolvedCommand!, ["--version"], cancellationToken: cts.Token),
                        cts.Token);
                    if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
                        version = stdout.Trim().Split('\n').FirstOrDefault()?.Trim();
                }
                catch
                {
                    version = null;
                }
            }

            var modelCount = await TryGetModelCountAsync(cts.Token);
            return new InfoVerboseOpencodeRuntime(commandAllowed ? resolvedCommand : null, version, modelCount, Resolved: commandAllowed);
        }
        catch
        {
            return new InfoVerboseOpencodeRuntime(null, null, null, Resolved: false);
        }
    }

    internal async Task<InfoVerboseOsRuntime> GetOsRuntimeVerboseAsync()
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        string? os = null;
        string? arch = null;
        string? dotnet = null;
        string? node = null;

        try { os = GetOsName(); } catch { }
        try { arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(); } catch { }
        try { dotnet = RuntimeInformation.FrameworkDescription; } catch { }
        try
        {
            var (exit, stdout, _) = await InfoCollector.WithTimeout(
                _commandExecutor.ExecuteAsync("node", ["--version"]),
                cts.Token);
            if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
                node = stdout.Trim().Split('\n').FirstOrDefault()?.Trim();
        }
        catch
        {
        }

        return new InfoVerboseOsRuntime(os, arch, dotnet, node);
    }

    private async Task<int?> TryGetModelCountAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _api.Http.GetAsync("/api/opencode/runtime", ct);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            if (stream.Length == 0)
                return null;
            var node = await System.Text.Json.Nodes.JsonNode.ParseAsync(stream, cancellationToken: ct);
            var models = node?["data"]?["models"] as System.Text.Json.Nodes.JsonArray;
            if (models is not null)
                return models.Count;
            var single = node?["data"]?["model"];
            if (single is System.Text.Json.Nodes.JsonValue)
                return 1;
            if (single is System.Text.Json.Nodes.JsonArray arr)
                return arr.Count;
        }
        catch
        {
        }
        return null;
    }

    private static (string? Command, bool Allowed) ValidateAgentCommand(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, false);
        var basename = Path.GetFileName(raw);
        if (string.IsNullOrEmpty(basename))
            return (null, false);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "opencode", "opencode.exe" };
        if (!allowed.Contains(basename))
            return (null, false);
        return (basename, true);
    }

    private static string? GetOsName()
    {
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "darwin";
        if (OperatingSystem.IsWindows()) return "windows";
        return "unknown";
    }
}
