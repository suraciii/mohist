namespace Mohist.Cli;

internal sealed class VerboseRunnerInspector
{
    private readonly ICommandExecutor _commandExecutor;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly MohistCliApi _api;

    public VerboseRunnerInspector(ICommandExecutor commandExecutor, IEnvironmentVariableProvider environment, MohistCliApi api)
    {
        _commandExecutor = commandExecutor;
        _environment = environment;
        _api = api;
    }

    internal async Task<IReadOnlyList<InfoVerboseEnvVar>> GetEnvVarsVerboseAsync(InfoService runner, bool systemdAvailable, IReadOnlyDictionary<string, string>? unitEnv = null)
    {
        var collected = new Dictionary<string, string?>(StringComparer.Ordinal);
        var envSource = unitEnv ?? await TryGetRunnerUnitEnvironmentAsync(systemdAvailable, CancellationToken.None);
        if (envSource is not null && runner.Status is { State: "active" })
        {
            foreach (var kvp in envSource)
            {
                if (WatchedEnvVarNames.Contains(kvp.Key, StringComparer.Ordinal))
                    collected[kvp.Key] = kvp.Value;
            }
        }

        foreach (var key in WatchedEnvVarNames)
        {
            if (!collected.ContainsKey(key))
            {
                var value = _environment.GetEnvironmentVariable(key);
                if (value is not null)
                    collected[key] = value;
            }
        }

        return collected
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new InfoVerboseEnvVar(kvp.Key, kvp.Value))
            .ToArray();
    }

    internal async Task<IReadOnlyDictionary<string, string>?> TryGetRunnerUnitEnvironmentAsync(bool systemdAvailable, CancellationToken ct)
    {
        if (!systemdAvailable)
            return null;
        try
        {
            var (exit, stdout, _) = await InfoCollector.WithTimeout(
                _commandExecutor.ExecuteAsync("systemctl", [
                    "--user",
                    "show",
                    SystemdUnitParser.RunnerUnit,
                    "-p",
                    "Environment",
                ], cancellationToken: ct),
                ct);
            if (exit != 0)
                return null;
            return SystemdUnitParser.ParseSystemdEnvironment(stdout);
        }
        catch
        {
            return null;
        }
    }

    internal async Task<InfoVerboseCapacity> GetCapacityVerboseAsync(
        InfoService runner,
        InfoProject? project,
        bool systemdAvailable,
        IReadOnlyDictionary<string, string>? unitEnv = null)
    {
        int? maxFromUnit = null;
        int? maxFromEnv = null;
        RunnerCapacity? serverCapacity = null;
        var envSource = unitEnv ?? await TryGetRunnerUnitEnvironmentAsync(systemdAvailable, CancellationToken.None);

        if (runner.Status is { State: "active" } && envSource is not null
            && envSource.TryGetValue("MAX_CONCURRENT_WORKFLOWS", out var maxText)
            && int.TryParse(maxText, out var parsed)
            && parsed > 0)
        {
            maxFromUnit = parsed;
        }

        var maxFromEnvText = _environment.GetEnvironmentVariable("MAX_CONCURRENT_WORKFLOWS");
        if (maxFromEnvText is not null && int.TryParse(maxFromEnvText, out var maxEnvParsed) && maxEnvParsed > 0)
            maxFromEnv = maxEnvParsed;

        if (project is not null && !string.IsNullOrWhiteSpace(project.Id))
            serverCapacity = await TryGetServerCapacityAsync(project.Id!);

        return new InfoVerboseCapacity(serverCapacity?.Active, maxFromUnit ?? serverCapacity?.Max ?? maxFromEnv);
    }

    private async Task<RunnerCapacity?> TryGetServerCapacityAsync(string projectId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var path = $"/api/projects/{Uri.EscapeDataString(projectId)}/agent/status";
            using var response = await _api.Http.GetAsync(path, cts.Token);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            if (stream.Length == 0)
                return null;
            var node = await System.Text.Json.Nodes.JsonNode.ParseAsync(stream, cancellationToken: cts.Token);
            var capacity = node?["data"]?["capacity"];
            if (capacity is null)
                return null;
            return new RunnerCapacity(
                capacity["active"]?.GetValue<int?>(),
                capacity["max"]?.GetValue<int?>());
        }
        catch
        {
            return null;
        }
    }

    private sealed record RunnerCapacity(int? Active, int? Max);

    private static readonly string[] WatchedEnvVarNames =
    {
        "MOHIST_AGENT_COMMAND",
        "MOHIST_DB_PATH",
        "MOHIST_GIT_HASH",
        "MOHIST_RUNNER_ROOT",
        "MOHIST_SERVER_URL",
        "MOHIST_SKILLS_DIR",
        "MOHIST_WORKSPACE_ROOT",
        "MOHIST_ARTIFACT_ROOT",
        "MOHIST_CONFIG__AGENT_COMMAND",
        "MAX_CONCURRENT_WORKFLOWS",
        "RUNNER_ID",
        "SERVER_URL",
    };
}
