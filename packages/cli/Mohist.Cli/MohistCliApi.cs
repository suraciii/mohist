using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class MohistCliApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };
    internal static JsonSerializerOptions JsonOutputOptions => JsonOptions;
    internal static JsonSerializerOptions JsonCompactOutputOptions { get; } = new(JsonOptions)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _http;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly IFileSystem _fileSystem;
    private readonly ICommandExecutor _commandExecutor;
    private readonly TextReader _standardInput;
    private readonly Func<string> _getUserHome;
    private readonly CliResponseReader _responseReader;
    private readonly ProjectReferenceResolver _projectReferenceResolver;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _pollWait;
    internal CliInvocation Invocation { get; }

    internal TextWriter Output => _out;
    internal TextWriter Error => _err;
    internal IFileSystem FileSystem => _fileSystem;
    internal ICommandExecutor CommandExecutor => _commandExecutor;
    internal TextReader StandardInput => _standardInput;
    internal HttpClient Http => _http;
    internal CliResponseReader ResponseReader => _responseReader;
    internal Func<string> GetUserHome => _getUserHome;
    internal TimeProvider TimeProvider => _timeProvider;
    internal Func<TimeSpan, CancellationToken, Task> PollWait => _pollWait;
    internal string CurrentProjectStatePath => ProjectReferenceResolver.StatePath(_fileSystem.CurrentDirectory);

    public MohistCliApi(
        HttpClient http,
        TextWriter output,
        TextWriter error,
        IFileSystem fileSystem,
        ICommandExecutor commandExecutor,
        TextReader? standardInput = null,
        Func<string>? getUserHome = null,
        CliResponseReader? responseReader = null,
        ICliTerminal? terminal = null,
        ICliEnvironment? cliEnvironment = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? pollWait = null,
        CancellationToken cancellationToken = default)
    {
        _http = http;
        _out = output;
        _err = error;
        _fileSystem = fileSystem;
        _commandExecutor = commandExecutor;
        _standardInput = standardInput ?? Console.In;
        _getUserHome = getUserHome ?? (fileSystem is RealFileSystem
            ? () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : () => "/mohist-tests/user");
        _responseReader = responseReader ?? new CliResponseReader(http);
        _projectReferenceResolver = new ProjectReferenceResolver(_fileSystem, _getUserHome);
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Polling waits (mo auth login) are released by the HTTP responder
        // sequence in tests; production sleeps the RFC 8628 interval.
        _pollWait = pollWait ?? ((delay, ct) => Task.Delay(delay, _timeProvider, ct));
        Invocation = new CliInvocation(
            output,
            error,
            _standardInput,
            terminal ?? CliTerminal.From(_standardInput),
            cliEnvironment ?? SystemCliEnvironment.Instance,
            cancellationToken);
    }

    public async Task<int> PrintGetAsync(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path, body: null);
        return response is null ? 1 : await PrintResponseAsync(response);
    }

    internal int WriteJsonSelectionResult(ResourceDescriptor descriptor, JsonSelection selection)
    {
        if (selection.Kind == JsonSelectionKind.Discovery)
        {
            _out.WriteLine(JsonSerializer.Serialize(selection.Fields));
            return 0;
        }

        if (selection.Kind == JsonSelectionKind.Invalid)
        {
            _err.WriteLine(
                $"Invalid --json field '{selection.InvalidField}'. Run this command with bare --json to list accepted fields.");
            return 2;
        }

        return 0;
    }

    internal async Task<int> PrintResourceAsync(
        string path,
        ResourceDescriptor descriptor,
        JsonSelection selection,
        Func<JsonNode?, Task<int>> humanRenderer,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = await ResponseReader.ReadAsync(
                HttpMethod.Get,
                path,
                headers: headers,
                cancellationToken: Invocation.CancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccess)
            return await new CliResultWriter(Invocation).WriteFailureAsync(response.Failure!).ConfigureAwait(false);

        try
        {
            if (selection.Kind == JsonSelectionKind.Selected)
            {
                var projected = selection.Project(response.Data, descriptor.Cardinality);
                return await new CliResultWriter(Invocation).WriteSuccessAsync(projected).ConfigureAwait(false);
            }

            return await humanRenderer(response.Data).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return await new CliResultWriter(Invocation).WriteFailureAsync(
                new CliFailure("invalid-response", ex.Message, null)).ConfigureAwait(false);
        }
    }

    // Effective Variables are a flat merged object (no `vars`/`stages` envelope),
    // so its selectable fields are the response's own top-level keys, discovered
    // after the fetch rather than declared up front. `--json` is therefore
    // resolved against the actual keys of the returned object.
    internal async Task<int> PrintEffectiveVariablesAsync(
        string path,
        bool jsonProvided,
        string? jsonValue,
        Func<JsonNode?, Task<int>> humanRenderer)
    {
        var response = await ResponseReader.ReadAsync(HttpMethod.Get, path, cancellationToken: Invocation.CancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccess)
            return await new CliResultWriter(Invocation).WriteFailureAsync(response.Failure!).ConfigureAwait(false);

        var descriptor = EffectiveDescriptorFor(response.Data);
        var selection = JsonSelection.Parse(descriptor, jsonProvided, jsonValue);
        if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
            return WriteJsonSelectionResult(descriptor, selection);

        try
        {
            if (selection.Kind == JsonSelectionKind.Selected)
            {
                var projected = selection.Project(response.Data, descriptor.Cardinality);
                return await new CliResultWriter(Invocation).WriteSuccessAsync(projected).ConfigureAwait(false);
            }

            return await humanRenderer(response.Data).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return await new CliResultWriter(Invocation).WriteFailureAsync(
                new CliFailure("invalid-response", ex.Message, null)).ConfigureAwait(false);
        }
    }

    private static ResourceDescriptor EffectiveDescriptorFor(JsonNode? data)
    {
        if (data is JsonObject obj)
        {
            var keys = new List<string>(obj.Count);
            foreach (var prop in obj)
                keys.Add(prop.Key);
            return new(ResourceCardinality.Single, keys);
        }
        return new(ResourceCardinality.Single, []);
    }

    internal async Task<int> PrintMutationResourceAsync(
        HttpMethod method,
        string path,
        object? body,
        ResourceDescriptor descriptor,
        JsonSelection selection,
        Func<JsonNode?, Task<int>> humanRenderer,
        JsonNode? successDataFallback = null,
        Func<JsonNode?, JsonNode?>? normalizeData = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = await ResponseReader.ReadAsync(
                method,
                path,
                body,
                mutating: true,
                headers: headers,
                cancellationToken: Invocation.CancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccess)
            return await new CliResultWriter(Invocation).WriteFailureAsync(response.Failure!).ConfigureAwait(false);

        try
        {
            var data = response.Data ?? successDataFallback;
            if (normalizeData is not null)
                data = normalizeData(data);
            if (selection.Kind == JsonSelectionKind.Selected)
            {
                var projected = selection.Project(data, descriptor.Cardinality);
                return await new CliResultWriter(Invocation).WriteSuccessAsync(projected).ConfigureAwait(false);
            }

            return await humanRenderer(data).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return await new CliResultWriter(Invocation).WriteFailureAsync(
                new CliFailure("invalid-response", ex.Message, null)).ConfigureAwait(false);
        }
    }

    public async Task<int> PrintProjectListAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/projects", body: null);
        if (response is null)
            return 1;

        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);

        if (node is null)
        {
            _out.WriteLine(response.StatusCode);
            return response.IsSuccessStatusCode ? 0 : 1;
        }

        var envelope = ExtractEnvelope(node, response);
        if (!envelope.Success)
        {
            WriteEnvelopeFailure(envelope);
            return FailureExitCode(response);
        }

        if (envelope.Data is not JsonArray array || array.Count == 0)
        {
            _out.WriteLine("No projects");
            return 0;
        }

        var activeProjectId = await ResolveProjectIdAsync(null);
        foreach (var item in array)
        {
            var id = item?["id"]?.GetValue<string>() ?? "";
            var name = item?["name"]?.GetValue<string>() ?? "";
            var marker = id == activeProjectId ? "* " : "  ";
            _out.WriteLine($"{marker}{name}");
        }

        return 0;
    }

    public async Task<int> PrintDeleteAsync(string path)
    {
        using var response = await SendAsync(HttpMethod.Delete, path, body: null);
        return response is null ? 1 : await PrintResponseAsync(response);
    }

    public async Task<int> PrintPostAsync(string path, object body)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body);
        return response is null ? 1 : await PrintResponseAsync(response);
    }

    public async Task<PostResult> PostAndReadAsync(string path, object body)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, printServerUnavailable: false);
        return await ReadPostResultAsync(response!);
    }

    public async Task<int> PrintPutAsync(string path, object body)
    {
        using var response = await SendAsync(HttpMethod.Put, path, body);
        return response is null ? 1 : await PrintResponseAsync(response);
    }

    public async Task<int> PrintPutWithOutputAsync(string path, object body, string mode, string? tableShape = null)
    {
        var localExit = HandleLocalJsonSelection(mode, tableShape);
        if (localExit is not null)
            return localExit.Value;
        using var response = await SendAsync(HttpMethod.Put, path, body);
        return response is null ? 1 : await PrintEnvelopeAsync(response, mode, tableShape);
    }

    public async Task<int> PrintPatchAsync(string path, object body)
    {
        using var response = await SendAsync(HttpMethod.Patch, path, body);
        return response is null ? 1 : await PrintResponseAsync(response);
    }

    public async Task<JsonNode?> GetDataAsync(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path, body: null, printServerUnavailable: false);
        return await ReadSuccessDataAsync(response!);
    }

    public async Task<(int ExitCode, JsonNode? Data)> GetDataOrPrintErrorAsync(string path)
    {
        try
        {
            return (0, await GetDataAsync(path));
        }
        catch (ApiResponseException ex)
        {
            WriteApiFailure(ex);
            return (FailureExitCode(ex.StatusCode), null);
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return (1, null);
        }
    }

    public async Task<int> PrintGetSafeAsync(string path)
    {
        try
        {
            return await PrintGetAsync(path);
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }
    }

    public async Task<JsonNode?> GetDataSafeAsync(string path)
    {
        try
        {
            return await GetDataAsync(path);
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return null;
        }
    }

    public async Task<int> PrintRunnerListAsync(string projectId, RunnerScopeFilter scope, string mode, bool colorEnabled)
    {
        var localExit = HandleLocalJsonSelection(mode, nameof(TableShape.RunnerList));
        if (localExit is not null)
            return localExit.Value;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _err.WriteLine(MohistCliCommands.NoActiveProjectMessage);
            return 1;
        }

        JsonNode? responseData;
        try
        {
            responseData = await GetDataAsync($"/api/projects/{Uri.EscapeDataString(projectId)}/runners");
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }
        catch (ApiResponseException ex)
        {
            WriteApiFailure(ex);
            return FailureExitCode(ex.StatusCode);
        }

        if (responseData is null)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }

        var runners = responseData["runners"] as JsonArray ?? new JsonArray();
        var filtered = new JsonArray();
        foreach (var runner in runners)
        {
            if (runner is not JsonObject obj) continue;
            var runnerScope = obj["scope"] as JsonObject;
            var type = StringOfNullable(runnerScope, "type");
            if (scope == RunnerScopeFilter.Global && !string.Equals(type, "global", StringComparison.OrdinalIgnoreCase))
                continue;
            if (scope == RunnerScopeFilter.Project && !string.Equals(type, "project", StringComparison.OrdinalIgnoreCase))
                continue;
            filtered.Add(obj.DeepClone());
        }

        if (mode.StartsWith("json:", StringComparison.Ordinal))
            return await WriteSelectedDataAsync(filtered, mode, nameof(TableShape.RunnerList));

        if (filtered.Count == 0)
        {
            _out.WriteLine("No runners connected");
            _out.WriteLine($"Start a runner: {RunnerStartHint}");
            return 0;
        }

        var activeProjectId = await TryReadActiveProjectIdAsync();
        var renderer = new TableRenderer(_out, activeProjectId);
        renderer.Render(filtered, TableShape.RunnerList, colorEnabled);
        return 0;
    }

    public async Task<int> PrintRunnerShowAsync(string projectId, string runnerIdEncoded, string mode)
    {
        var localExit = HandleLocalJsonSelection(mode, nameof(TableShape.RunnerShow));
        if (localExit is not null)
            return localExit.Value;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _err.WriteLine(MohistCliCommands.NoActiveProjectMessage);
            return 1;
        }

        var path = $"/api/projects/{Uri.EscapeDataString(projectId)}/runners/{runnerIdEncoded}";

        using var response = await SendAsync(HttpMethod.Get, path, body: null);
        if (response is null)
            return 1;

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _err.WriteLine($"Runner '{runnerIdEncoded}' not found");
            return 1;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);

        var envelope = ExtractEnvelope(node, response);
        if (!envelope.Success)
        {
            WriteEnvelopeFailure(envelope);
            return FailureExitCode(response);
        }

        var runner = envelope.Data?["runner"] as JsonObject;
        if (runner is null)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }

        if (mode.StartsWith("json:", StringComparison.Ordinal))
            return await WriteSelectedDataAsync(runner, mode, nameof(TableShape.RunnerShow));

        RenderRunnerShow(runner);
        return 0;
    }

    public async Task<int> PrintRunnerStatusAsync(string projectId, string mode)
    {
        var localExit = HandleLocalJsonSelection(mode, nameof(TableShape.RunnerList));
        if (localExit is not null)
            return localExit.Value;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _err.WriteLine(MohistCliCommands.NoActiveProjectMessage);
            return 1;
        }

        JsonNode? data;
        try
        {
            data = await GetDataAsync($"/api/projects/{Uri.EscapeDataString(projectId)}/runners");
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }
        catch (ApiResponseException ex)
        {
            WriteApiFailure(ex);
            return FailureExitCode(ex.StatusCode);
        }

        if (data is null)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }

        var runners = data["runners"] as JsonArray ?? new JsonArray();

        if (mode.StartsWith("json:", StringComparison.Ordinal))
            return await WriteSelectedDataAsync(runners, mode, nameof(TableShape.RunnerList));

        if (runners.Count == 0)
        {
            _out.WriteLine("No runners connected");
            return 0;
        }

        var renderer = new TableRenderer(_out, projectId);
        renderer.RenderRunnerStatus(runners);
        return 0;
    }

    private void RenderRunnerShow(JsonObject runner)
    {
        _out.WriteLine($"Runner: {StringOfNullable(runner, "id")}");
        _out.WriteLine();
        _out.WriteLine("Identity");
        WriteKeyValue("  kind", StringOfNullable(runner, "kind"));
        WriteKeyValue("  hostname", StringOfNullable(runner, "hostname"));
        var scope = runner["scope"] as JsonObject;
        var scopeType = StringOfNullable(scope, "type");
        var scopeDetail = scopeType == "project"
            ? $"{scopeType} ({StringOfNullable(scope, "projectId")})"
            : scopeType;
        WriteKeyValue("  scope", scopeDetail);
        WriteKeyValue("  registeredAt", StringOfNullable(runner, "registeredAt"));
        WriteKeyValue("  buildGitHash", StringOfNullable(runner, "buildGitHash"));

        _out.WriteLine();
        _out.WriteLine("Capabilities");
        var caps = (runner["capabilities"] as JsonArray ?? new JsonArray())
            .Select(v => v?.GetValue<string>() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        WriteKeyValue("  capabilities", caps.Count == 0 ? "(none)" : string.Join(", ", caps));
        var coderModels = (runner["coderModels"] as JsonArray ?? new JsonArray())
            .Select(v => v?.GetValue<string>() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        var coderModelCount = runner["coderModelCount"]?.GetValue<int?>() ?? coderModels.Count;
        WriteKeyValue("  coderModels", coderModels.Count == 0 ? "(none)" : $"{string.Join(", ", coderModels)} ({coderModelCount})");
        var capacity = runner["capacity"] as JsonObject;
        var maxSlots = capacity?["totalSlots"]?.GetValue<int?>();
        WriteKeyValue("  maxWorkflowSlots", maxSlots?.ToString() ?? "(unknown)");

        _out.WriteLine();
        _out.WriteLine("Active Works");
        var works = runner["activeWorks"] as JsonArray ?? new JsonArray();
        if (works.Count == 0)
        {
            _out.WriteLine("  (no active works)");
        }
        else
        {
            for (var i = 0; i < works.Count; i++)
            {
                if (works[i] is not JsonObject work) continue;
                _out.WriteLine($"  [{i + 1}] workId: {StringOfNullable(work, "workId")}");
                WriteKeyValue("      workType", StringOfNullable(work, "workType"));
                var ownerKind = StringOfNullable(work, "ownerKind");
                var ownerId = StringOfNullable(work, "ownerId");
                WriteKeyValue("      owner", string.IsNullOrEmpty(ownerId) ? ownerKind : $"{ownerKind} {ownerId}");
                WriteKeyValue("      stage", StringOfNullable(work, "stage"));
                WriteKeyValue("      title", StringOfNullable(work, "title"));
                if (work["issue"] is JsonObject issue)
                {
                    var issueNumber = issue["issueNumber"]?.GetValue<int?>();
                    WriteKeyValue("      issue", issueNumber?.ToString() ?? "");
                }
            }
        }

        _out.WriteLine();
        _out.WriteLine("Health");
        WriteKeyValue("  status", StringOfNullable(runner, "status"));
        WriteKeyValue("  connectionState", StringOfNullable(runner, "connectionState"));
        WriteKeyValue("  lastHeartbeatAt", StringOfNullable(runner, "lastHeartbeatAt"));
        if (capacity is not null)
        {
            var used = capacity["usedSlots"]?.GetValue<int?>();
            var total = capacity["totalSlots"]?.GetValue<int?>();
            if (used is not null && total is not null)
                WriteKeyValue("  capacity", $"{used}/{total} slots");
        }
    }

    public async Task<int> PrintSystemInfoAsync(string mode)
    {
        var localExit = HandleLocalJsonSelection(mode, nameof(TableShape.SystemInfo));
        if (localExit is not null)
            return localExit.Value;
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync("/api/system/info");
        }
        catch (HttpRequestException)
        {
            await RenderSystemInfoDegradedAsync(mode);
            return 0;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);

        if (node is null)
        {
            await RenderSystemInfoDegradedAsync(mode);
            return 0;
        }

        var envelope = ExtractEnvelope(node, response);
        if (!envelope.Success)
        {
            WriteEnvelopeFailure(envelope);
            return FailureExitCode(response);
        }

        var data = envelope.Data;

        if (mode.StartsWith("json:", StringComparison.Ordinal))
            return await WriteSelectedDataAsync(data, mode, nameof(TableShape.SystemInfo));

        if (data is JsonObject obj)
        {
            RenderSystemInfo(obj);
            return 0;
        }

        _out.WriteLine(data is null ? "(no data)" : data.ToJsonString(JsonOptions));
        return 0;
    }

    private async Task RenderSystemInfoDegradedAsync(string mode)
    {
        var cliVersion = TryGetCliVersion();

        _err.WriteLine(ServerUnavailableMessage);

        var payload = new JsonObject
        {
            ["running"] = null,
            ["source"] = null,
            ["install"] = null,
            ["update"] = null,
            ["services"] = null,
            ["paths"] = null,
            ["cliVersion"] = cliVersion,
            ["degraded"] = true,
        };

        if (mode.StartsWith("json:", StringComparison.Ordinal))
        {
            await WriteSelectedDataAsync(payload, mode, nameof(TableShape.SystemInfo));
            return;
        }

        if (string.Equals(mode, "json", StringComparison.Ordinal))
        {
            _out.WriteLine(payload.ToJsonString(JsonOptions));
            return;
        }

        _out.WriteLine("Server diagnostics unavailable (server not reachable).");
        _out.WriteLine();
        _out.WriteLine("CLI (local)");
        WriteKeyValue("  version", cliVersion ?? "<unknown>");
    }

    internal void RenderSystemInfo(JsonObject data)
    {
        var running = data["running"] as JsonObject;
        var source = data["source"] as JsonObject;
        var install = data["install"] as JsonObject;
        var update = data["update"] as JsonObject;
        var services = data["services"] as JsonObject;
        var paths = data["paths"] as JsonObject;

        _out.WriteLine("Identity");
        WriteKeyValue("  version", StringOfNullable(running, "version"));
        WriteKeyValue("  gitHash", StringOfNullable(running, "gitHash"));
        WriteKeyValue("  startedAt", StringOfNullable(running, "startedAt"));

        _out.WriteLine();
        _out.WriteLine("Source");
        WriteKeyValue("  path", StringOfNullable(source, "path"));
        WriteKeyValue("  branch", StringOfNullable(source, "branch"));
        WriteKeyValue("  head", StringOfNullable(source, "head"));
        WriteKeyValue("  dirty", BoolOfNullable(source, "dirty")?.ToString().ToLowerInvariant() ?? "");

        _out.WriteLine();
        _out.WriteLine("Install");
        WriteKeyValue("  mode", StringOfNullable(install, "mode"));
        WriteKeyValue("  serviceManager", StringOfNullable(install, "serviceManager"));
        WriteKeyValue("  serverUnit", StringOfNullable(install, "serverUnit"));
        WriteKeyValue("  runnerUnit", StringOfNullable(install, "runnerUnit"));
        WriteKeyValue("  reason", StringOfNullable(install, "reason"));

        _out.WriteLine();
        _out.WriteLine("Update");
        WriteKeyValue("  status", StringOfNullable(update, "status"));
        WriteKeyValue("  available", BoolOfNullable(update, "available")?.ToString().ToLowerInvariant() ?? "");
        WriteKeyValue("  reason", StringOfNullable(update, "reason"));

        _out.WriteLine();
        _out.WriteLine("Services");
        WriteKeyValue("  server", StringOfNullable(services, "server"));
        WriteKeyValue("  runner", StringOfNullable(services, "runner"));

        _out.WriteLine();
        _out.WriteLine("Paths");
        WriteKeyValue("  db", StringOfNullable(paths, "db"));
        WriteKeyValue("  config", StringOfNullable(paths, "config"));
        WriteKeyValue("  logs", StringOfNullable(paths, "logs"));
        WriteKeyValue("  opencode", StringOfNullable(paths, "opencode"));
    }

    private static string? TryGetCliVersion()
    {
        try
        {
            var assembly = typeof(MohistCliApi).Assembly;
            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                var plusIndex = informationalVersion.IndexOf('+');
                if (plusIndex > 0)
                    return informationalVersion[..plusIndex];
                return informationalVersion;
            }

            return assembly.GetName().Version?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool? BoolOfNullable(JsonNode? node, string key)
    {
        if (node is null) return null;
        var value = node[key];
        if (value is null) return null;
        return value.GetValue<bool>();
    }

    private void WriteKeyValue(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
            _out.WriteLine($"{key}:");
        else
            _out.WriteLine($"{key}: {value}");
    }

    public enum RunnerScopeFilter
    {
        All,
        Global,
        Project,
    }

    private static string StringOfNullable(JsonNode? node, string key)
    {
        if (node is null) return "";
        var value = node[key];
        if (value is null) return "";
        return value.GetValue<string>() ?? "";
    }

    internal const string ServerUnavailableMessage = "Server is not running. Start with: mo service start server";
    internal const string RunnerStartHint = "npx mohist runner";

    public abstract record OutputModeResult
    {
        private OutputModeResult() { }

        public sealed record Valid(string Mode) : OutputModeResult;

        public sealed record Invalid(string Message) : OutputModeResult;
    }

    public static OutputModeResult ValidateOutputMode(string? mode)
    {
        if (string.Equals(mode, "table", StringComparison.Ordinal)
            && !MohistCliCommands.OutputOptionState.Explicit)
            return new OutputModeResult.Valid("table");

        if (string.Equals(mode, "table", StringComparison.Ordinal))
            return new OutputModeResult.Valid("discover");

        if (string.Equals(mode, "json", StringComparison.Ordinal))
            return new OutputModeResult.Valid("discover");

        if (!string.IsNullOrWhiteSpace(mode))
            return new OutputModeResult.Valid($"json:{mode}");

        return new OutputModeResult.Valid("table");
    }

    public (string Mode, int Exit) ResolveOutputMode(string? output)
    {
        var validation = ValidateOutputMode(output);
        if (validation is OutputModeResult.Invalid invalid)
        {
            _err.WriteLine(invalid.Message);
            return ("json", 1);
        }
        return (((OutputModeResult.Valid)validation).Mode, 0);
    }

    public async Task<(string ProjectId, int Exit)> ResolveProject(string? project)
    {
        var result = await _projectReferenceResolver
            .ResolveAsync(project)
            .ConfigureAwait(false);
        return result switch
        {
            ProjectReferenceResolver.Result.Resolved resolved => (resolved.ProjectReference, 0),
            ProjectReferenceResolver.Result.Invalid invalid => (ReportProjectResolutionFailure(
                $"Invalid {invalid.Source}. Run 'mo project use <name-or-id>' or pass --project <name-or-id>."), 1),
            _ => (ReportProjectResolutionFailure(
                MohistCliCommands.NoActiveProjectMessage), 1),
        };
    }

    private string ReportProjectResolutionFailure(string message)
    {
        _err.WriteLine(message);
        return "";
    }

    public async Task<int> PrintWithOutputAsync(
        string path,
        string mode,
        string? tableShape = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var localExit = HandleLocalJsonSelection(mode, tableShape);
        if (localExit is not null)
            return localExit.Value;
        using var response = await SendAsync(HttpMethod.Get, path, body: null, headers: headers);
        return response is null ? 1 : await PrintEnvelopeAsync(response, mode, tableShape);
    }

    public async Task<int> PrintPostWithOutputAsync(
        string path,
        object? body,
        string mode,
        string? tableShape = null,
        bool rawJson = false,
        IReadOnlyDictionary<string, string>? headers = null,
        int retries = 0)
    {
        var localExit = HandleLocalJsonSelection(mode, tableShape);
        if (localExit is not null)
            return localExit.Value;
        using var response = await SendAsync(HttpMethod.Post, path, body, headers: headers, retries: retries);
        return response is null ? 1 : await PrintEnvelopeAsync(response, mode, tableShape, rawJson: rawJson);
    }

    public async Task<int> PrintPatchWithOutputAsync(string path, object body, string mode, string? tableShape = null)
    {
        var localExit = HandleLocalJsonSelection(mode, tableShape);
        if (localExit is not null)
            return localExit.Value;
        using var response = await SendAsync(HttpMethod.Patch, path, body);
        return response is null ? 1 : await PrintEnvelopeAsync(response, mode, tableShape);
    }

    public Task<int> CreateWebhookSubscriptionAsync(
        string projectId,
        string name,
        string? match,
        string targetUrl,
        string? eventSelectionMode,
        IReadOnlyList<string>? eventTypes,
        string? authType,
        string? authToken,
        (string User, string Password)? authBasic,
        IReadOnlyDictionary<string, string>? authHeaders,
        string? legacySecret,
        string mode)
    {
        object body = new
        {
            name,
            match,
            targetUrl,
            eventSelectionMode,
            eventTypes,
            authType,
            authToken,
            authBasic = authBasic is { } b ? new { user = b.User, password = b.Password } : null,
            authHeaders,
            secret = legacySecret,
        };
        return PrintPostWithOutputAsync(
            WebhookSubscriptionsPath(projectId),
            body,
            mode,
            nameof(TableShape.WebhookSubscription));
    }

    public Task<int> ListWebhookEventTypesAsync(string projectId, string mode) =>
        PrintWithOutputAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/webhook/event-types",
            mode,
            tableShape: null);

    public async Task<(JsonNode? Data, int Exit)> ConnectGitHubRepositoryAsync(
        string projectId,
        string owner,
        string repo,
        string? feedMode,
        IReadOnlyList<string>? approvers)
    {
        try
        {
            var data = await PostDataAsync(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/github-connections",
                new
                {
                    owner,
                    repo,
                    feedMode,
                    approvers = approvers is { Count: > 0 } ? approvers : null,
                }).ConfigureAwait(false);
            if (data is JsonObject obj)
            {
                var id = obj["id"]?.GetValue<string>() ?? string.Empty;
                obj["ingressUrl"] = GitHubIngressUrl(id);
            }
            return (data, 0);
        }
        catch (ApiResponseException ex)
        {
            WriteApiFailure(ex);
            return (null, FailureExitCode(ex.StatusCode));
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return (null, 1);
        }
    }

    public async Task<(JsonNode? Data, int Exit)> UpdateGitHubConnectionApproversAsync(
        string projectId,
        string connectionId,
        IReadOnlyList<string>? approvers,
        bool clearApprovers)
    {
        try
        {
            var data = await PatchDataAsync(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/github-connections/{Uri.EscapeDataString(connectionId)}",
                new
                {
                    approvers = clearApprovers
                        ? Array.Empty<string>()
                        : approvers is { Count: > 0 } ? approvers : null,
                }).ConfigureAwait(false);
            return (data, 0);
        }
        catch (ApiResponseException ex)
        {
            WriteApiFailure(ex);
            return (null, FailureExitCode(ex.StatusCode));
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return (null, 1);
        }
    }

    private string GitHubIngressUrl(string connectionId)
    {
        var relative = $"/api/github-connections/{Uri.EscapeDataString(connectionId)}/ingress";
        return _http.BaseAddress is null ? relative : new Uri(_http.BaseAddress, relative).ToString();
    }

    public Task<int> ListWebhookSubscriptionsAsync(string projectId, string mode, bool includeArchived = false) =>
        PrintWithOutputAsync(
            WebhookSubscriptionsPath(projectId, includeArchived),
            mode,
            nameof(TableShape.WebhookSubscriptionList));

    public Task<int> ViewWebhookSubscriptionAsync(string projectId, string subscriptionId, string mode) =>
        PrintWithOutputAsync(
            WebhookSubscriptionPath(projectId, subscriptionId),
            mode,
            nameof(TableShape.WebhookSubscription));

    public Task<int> UpdateWebhookSubscriptionAsync(
        string projectId,
        string subscriptionId,
        JsonObject updates,
        string mode) =>
        PrintPatchWithOutputAsync(
            WebhookSubscriptionPath(projectId, subscriptionId),
            updates,
            mode,
            nameof(TableShape.WebhookSubscription));

    public Task<int> ChangeWebhookSubscriptionStatusAsync(
        string projectId,
        string subscriptionId,
        string action,
        string mode) =>
        PrintPostWithOutputAsync(
            $"{WebhookSubscriptionPath(projectId, subscriptionId)}/{Uri.EscapeDataString(action)}",
            new { },
            mode,
            nameof(TableShape.WebhookSubscription));

    public Task<int> RotateWebhookSubscriptionSecretAsync(
        string projectId,
        string subscriptionId,
        string secret,
        string mode) =>
        PrintPostWithOutputAsync(
            $"{WebhookSubscriptionPath(projectId, subscriptionId)}/rotate-secret",
            new { secret },
            mode,
            nameof(TableShape.WebhookSubscription));

    public Task<int> ListWebhookDeliveryFailuresAsync(
        string projectId,
        string mode,
        string? subscriptionId = null) =>
        PrintWithOutputAsync(
            subscriptionId is null
                ? $"{WebhookSubscriptionsPath(projectId)}/failures"
                : $"{WebhookSubscriptionPath(projectId, subscriptionId)}/failures",
            mode,
            nameof(TableShape.WebhookDeliveryFailureList));

    private static string WebhookSubscriptionsPath(string projectId, bool includeArchived = false) =>
        $"/api/projects/{Uri.EscapeDataString(projectId)}/webhook/subscriptions{(includeArchived ? "?all=true" : string.Empty)}";

    private static string WebhookSubscriptionPath(string projectId, string subscriptionId) =>
        $"{WebhookSubscriptionsPath(projectId)}/{Uri.EscapeDataString(subscriptionId)}";

    public async Task<int> PrintDeleteWithOutputAsync(string path, string mode, string? tableShape = null, JsonNode? successDataFallback = null)
    {
        var localExit = HandleLocalJsonSelection(mode, tableShape);
        if (localExit is not null)
            return localExit.Value;
        using var response = await SendAsync(HttpMethod.Delete, path, body: null);
        return response is null ? 1 : await PrintEnvelopeAsync(response, mode, tableShape, successDataFallback);
    }

    private async Task<int> PrintEnvelopeAsync(HttpResponseMessage response, string mode, string? tableShape, JsonNode? successDataFallback = null, bool rawJson = false)
    {
        if (string.Equals(mode, "json", StringComparison.Ordinal))
        {
            if (rawJson)
                return await PrintRawResponseAsync(response);
            if (successDataFallback is not null)
                return await PrintResponseAsync(response, successDataFallback);
            return await PrintResponseAsync(response);
        }

        if (!response.IsSuccessStatusCode)
            return await PrintResponseAsync(response);

        var data = await ReadSuccessDataAsync(response) ?? successDataFallback?.DeepClone();
        if (string.Equals(tableShape, nameof(TableShape.RepoList), StringComparison.Ordinal)
            && data is JsonObject project
            && project["repositories"] is JsonArray repositories)
        {
            data = repositories;
        }
        if (mode.StartsWith("json:", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(tableShape))
                return await PrintResponseAsync(response);
            var descriptor = ResourceOutputCatalog.For(tableShape);
            var selection = JsonSelection.Parse(descriptor, true, mode[5..]);
            if (selection.Kind == JsonSelectionKind.Invalid)
                return WriteJsonSelectionResult(descriptor, selection);
            try
            {
                return await new CliResultWriter(Invocation).WriteSuccessAsync(
                    selection.Project(data, descriptor.Cardinality)).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return await new CliResultWriter(Invocation).WriteFailureAsync(
                    new CliFailure("invalid-response", ex.Message, null)).ConfigureAwait(false);
            }
        }
        var shape = ParseTableShape(tableShape);
        return await RenderTableAsync(data, shape);
    }

    internal int? HandleLocalJsonSelection(string mode, string? tableShape)
    {
        if (mode == "table")
            return null;

        if (string.IsNullOrWhiteSpace(tableShape))
            return null;

        var descriptor = ResourceOutputCatalog.For(tableShape);
        if (mode == "discover")
            return WriteJsonSelectionResult(descriptor, new JsonSelection(JsonSelectionKind.Discovery, descriptor.Fields, null));

        if (!mode.StartsWith("json:", StringComparison.Ordinal))
            return null;

        var selection = JsonSelection.Parse(descriptor, true, mode[5..]);
        return selection.Kind == JsonSelectionKind.Invalid
            ? WriteJsonSelectionResult(descriptor, selection)
            : null;
    }

    internal async Task<int> WriteSelectedDataAsync(JsonNode? data, string mode, string? tableShape)
    {
        if (mode == "discover")
            return WriteJsonSelectionResult(ResourceOutputCatalog.For(tableShape), new JsonSelection(JsonSelectionKind.Discovery, ResourceOutputCatalog.For(tableShape).Fields, null));
        if (!mode.StartsWith("json:", StringComparison.Ordinal))
            return await RenderTableAsync(data, ParseTableShape(tableShape)).ConfigureAwait(false);

        var descriptor = ResourceOutputCatalog.For(tableShape);
        var selection = JsonSelection.Parse(descriptor, true, mode[5..]);
        if (selection.Kind == JsonSelectionKind.Invalid)
            return WriteJsonSelectionResult(descriptor, selection);
        try
        {
            return await new CliResultWriter(Invocation).WriteSuccessAsync(
                selection.Project(data, descriptor.Cardinality)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return await new CliResultWriter(Invocation).WriteFailureAsync(
                new CliFailure("invalid-response", ex.Message, null)).ConfigureAwait(false);
        }
    }

    internal Task<int> WriteJsonDataAsync(JsonNode? data) =>
        new CliResultWriter(Invocation).WriteSuccessAsync(data);

    public enum TableShape
    {
        ProjectList,
        Project,
        IssueList,
        Issue,
        WorkflowStatus,
        Sessions,
        RepoList,
        FeedbackList,
        FeedbackShow,
        CommentShow,
        AgentList,
        AgentShow,
        EpicList,
        EpicShow,
        EpicLink,
        EpicUnlink,
        LabelList,
        IssueTemplateList,
        IssueTemplateShow,
        WorkflowProfileList,
        WorkflowProfileDetail,
        RunnerList,
        RunnerShow,
        Models,
        SystemInfo,
        WorkflowProfile,
        WorkflowVariables,
        WorkflowProfilePrompt,
        WorkflowProfilePreview,
        SessionMetadata,
        SessionTranscriptSummary,
        SessionRecovery,
        AgentSessionLaunch,
        AgentSessionSpawn,
        AgentSessionFollowup,
        AgentSessionStop,
        AgentSessionList,
        AgentSessionShow,
        AgentSessionTranscript,
        AgentSubscriptionList,
        AgentSubscription,
        RoutingRuleList,
        RoutingRule,
        ProjectTemplateList,
        ProjectTemplateShow,
        ProjectWorkflowProfile,
        IssueArchiveCompleted,
        WorkflowRunDetail,
        WorkflowApproval,
        WorkflowRunVariables,
        WorkflowRunEvents,
        RunList,
        DeadLetterList,
        DeadLetterRedelivery,
        ActivityList,
        IssueWatchList,
        AgentJobList,
        AgentJobView, AgentLaunchObservation,
        SessionList,
        SessionShow,
        SessionTree,
        SessionTranscript,
        SessionFollowup,
        SessionStop,
        SessionDetach,
        SessionScheduleCreate,
        SessionScheduleList,
        SessionScheduleCancel,
        OtelTracesList,
        WebhookSubscriptionList,
        WebhookSubscription,
        WebhookDeliveryFailureList,
        WorkspaceList,
        WorkspaceShow,
    }

    internal static TableShape ParseTableShape(string? shape)
    {
        if (string.IsNullOrWhiteSpace(shape))
            return TableShape.ProjectList;
        return Enum.TryParse<TableShape>(shape, ignoreCase: false, out var parsed)
            ? parsed
            : TableShape.ProjectList;
    }

    public async Task<int> RenderTableAsync(JsonNode? data, TableShape shape)
    {
        var activeProjectId = await TryReadActiveProjectIdAsync();
        var renderer = new TableRenderer(_out, activeProjectId);
        renderer.Render(data, shape);
        return 0;
    }

    /// <summary>
    /// Renders the Server-authoritative Availability + waiting-work block for
    /// <c>mo agent view</c> on top of <see cref="RenderTableAsync"/>. The
    /// renderer consumes the AgentStatusDetailResponse payload and does not
    /// synthesize availability from raw runner data.
    /// </summary>
    public async Task RenderAgentShowAvailabilityAsync(JsonNode? data)
    {
        var activeProjectId = await TryReadActiveProjectIdAsync();
        var renderer = new TableRenderer(_out, activeProjectId);
        renderer.RenderAgentShowStatus(data);
    }

    public async Task RenderAgentShowAsync(JsonNode? data)
    {
        var activeProjectId = await TryReadActiveProjectIdAsync();
        var renderer = new TableRenderer(_out, activeProjectId);
        renderer.Render(data, TableShape.AgentShow);
    }

    public async Task<JsonNode?> PostDataAsync(string path, object body)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, printServerUnavailable: false);
        return await ReadSuccessDataAsync(response!);
    }

    internal async Task<JsonNode?> PatchDataAsync(string path, object body)
    {
        using var response = await SendAsync(HttpMethod.Patch, path, body, printServerUnavailable: false);
        return await ReadSuccessDataAsync(response!);
    }

    internal async Task<JsonNode?> UploadAttachmentAsync(string projectId, string path)
    {
        using var stream = _fileSystem.OpenRead(path);
        using var form = new MultipartFormDataContent();
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue(AgentAttachmentInput.ContentTypeFor(path));
        form.Add(file, "file", Path.GetFileName(path));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{Uri.EscapeDataString(projectId)}/attachments")
        {
            Content = form,
        };
        using var response = await _http.SendAsync(request, Invocation.CancellationToken).ConfigureAwait(false);
        return await ReadSuccessDataAsync(response).ConfigureAwait(false);
    }

    public async Task<int> PrintWorkflowProfilesDescribedAsync(string? projectId = null, string mode = "table")
    {
        var path = projectId is not null
            ? $"/api/workflow-profiles?project={Uri.EscapeDataString(projectId)}"
            : "/api/workflow-profiles";
        try
        {
            var data = await GetDataAsync(path);
            if (mode.StartsWith("json:", StringComparison.Ordinal))
                return await WriteSelectedDataAsync(data, mode, nameof(TableShape.WorkflowProfileList));
            RenderWorkflowProfilesDescribed(data);
            return 0;
        }
        catch (ApiResponseException ex)
        {
            WriteApiFailure(ex);
            return FailureExitCode(ex.StatusCode);
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }
    }

    private void RenderWorkflowProfilesDescribed(JsonNode? data)
    {
        if (data is not JsonArray profiles || profiles.Count == 0)
        {
            _out.WriteLine("No workflow profiles found.");
            return;
        }

        foreach (var profile in profiles)
        {
            var id = profile?["id"]?.GetValue<string>() ?? "";
            var displayName = profile?["displayName"]?.GetValue<string>() ?? "";
            var description = profile?["description"]?.GetValue<string>() ?? "";
            _out.WriteLine($"{id} — {displayName}");
            _out.WriteLine($"  {description}");
            _out.WriteLine();
        }
    }

    public async Task<int> UseProjectAsync(string identifier)
    {
        try
        {
            var data = await PostDataAsync($"/api/projects/{Uri.EscapeDataString(identifier)}/use", new { });
            var id = data?["id"]?.GetValue<string>();
            var name = data?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                _err.WriteLine("Project response did not include an id");
                return 1;
            }

            var state = new JsonObject
            {
                ["activeProjectId"] = id,
            };
            var serializedState = state.ToJsonString(JsonOptions);
            await _fileSystem.WriteAllTextAsync(ProjectStatePath, serializedState);
            await _fileSystem.WriteAllTextAsync(CurrentProjectStatePath, serializedState);
            _out.WriteLine($"Active project: {name ?? id} ({id})");
            return 0;
        }
        catch (ApiResponseException ex)
        {
            WriteApiFailure(ex);
            return FailureExitCode(ex.StatusCode);
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }
    }

    public async Task<string?> ResolveProjectIdAsync(string? project)
    {
        if (!string.IsNullOrWhiteSpace(project))
            return project!;

        if (!_fileSystem.Exists(ProjectStatePath))
        {
            _err.WriteLine(MohistCliCommands.NoActiveProjectMessage);
            return null;
        }

        try
        {
            var json = await _fileSystem.ReadAllTextAsync(ProjectStatePath);
            var state = JsonNode.Parse(json);
            var active = state?["activeProjectId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(active))
            {
                _err.WriteLine(MohistCliCommands.NoActiveProjectMessage);
                return null;
            }
            return active;
        }
        catch
        {
            _err.WriteLine(MohistCliCommands.NoActiveProjectMessage);
            return null;
        }
    }

    internal Func<string>? ProjectStatePathOverride { get; set; }

    internal string ProjectStatePath => ProjectStatePathOverride is not null
        ? ProjectStatePathOverride()
        : Path.Combine(
            _getUserHome(),
            ".mohist",
            "cli-state.json");

    internal async Task<string?> TryReadActiveProjectIdAsync()
    {
        if (!_fileSystem.Exists(ProjectStatePath))
            return null;
        try
        {
            var json = await _fileSystem.ReadAllTextAsync(ProjectStatePath);
            var state = JsonNode.Parse(json);
            return state?["activeProjectId"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonNode?> ReadSuccessDataAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);

        if (node is null)
            throw new ApiResponseException(response.StatusCode, response.ReasonPhrase ?? "Request failed");

        var envelope = ExtractEnvelope(node, response);
        if (envelope.Success)
            return envelope.Data;

        throw new ApiResponseException(response.StatusCode, envelope.Error, envelope.Code, envelope.Details);
    }

    private async Task<int> PrintResponseAsync(HttpResponseMessage response, JsonNode? successDataFallback = null)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);

        if (node is null)
        {
            _out.WriteLine(response.StatusCode);
            return response.IsSuccessStatusCode ? 0 : 1;
        }

        var envelope = ExtractEnvelope(node, response);
        if (!envelope.Success)
        {
            WriteEnvelopeFailure(envelope);
            return FailureExitCode(response);
        }

        var data = envelope.Data ?? successDataFallback;
        _out.WriteLine(data is null ? "OK" : data.ToJsonString(JsonOptions));
        return 0;
    }

    /// <summary>
    /// Public entry point so commands can print the body of a Server response
    /// (success or failure) without exposing the full private surface.
    /// </summary>
    public Task<int> PrintServerResponseAsync(
        HttpResponseMessage response,
        JsonNode? successDataFallback = null,
        string? mode = null,
        string? tableShape = null) =>
        mode is null
            ? PrintResponseAsync(response, successDataFallback)
            : PrintEnvelopeAsync(response, mode, tableShape, successDataFallback);

    private async Task<int> PrintRawResponseAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);

        if (node is null)
        {
            _out.WriteLine(response.StatusCode);
            return response.IsSuccessStatusCode ? 0 : 1;
        }

        var envelope = ExtractEnvelope(node, response);
        if (!envelope.Success)
        {
            WriteEnvelopeFailure(envelope);
            return FailureExitCode(response);
        }

        _out.WriteLine(node.ToJsonString(JsonOptions));
        return 0;
    }

    /// <summary>
    /// Public entry point for the raw-response path so commands can stream
    /// the unmodified Server envelope to stdout (used by launch, which needs
    /// to inspect the envelope for 409 needs-setup before deciding how to
    /// format it).
    /// </summary>
    public Task<int> PrintRawServerResponseAsync(HttpResponseMessage response) =>
        PrintRawResponseAsync(response);

    public sealed record PostResult(int ExitCode, JsonNode? Data, string? Error, string? Code);

    private async Task<PostResult> ReadPostResultAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);

        if (node is null)
        {
            var statusOk = response.IsSuccessStatusCode;
            _out.WriteLine(response.StatusCode.ToString());
            return new PostResult(statusOk ? 0 : 1, null, statusOk ? null : response.ReasonPhrase, null);
        }

        var envelope = ExtractEnvelope(node, response);
        if (!envelope.Success)
        {
            WriteEnvelopeFailure(envelope);
            return new PostResult(FailureExitCode(response), null, envelope.Error, envelope.Code);
        }

        var data = envelope.Data;
        _out.WriteLine(data is null ? "OK" : data.ToJsonString(JsonOptions));
        return new PostResult(0, data, null, null);
    }

    private void WriteApiFailure(ApiResponseException failure)
    {
        var details = failure.Details is null ? string.Empty : $" details={failure.Details.ToJsonString()}";
        var code = string.IsNullOrWhiteSpace(failure.Code) ? $"http-{(int)failure.StatusCode}" : failure.Code;
        _err.WriteLine($"{failure.Message} (code={code}){details}");
    }

    internal sealed class ApiResponseException : Exception
    {
        public ApiResponseException(HttpStatusCode statusCode, string message, string? code = null, JsonNode? details = null) : base(message)
        {
            StatusCode = statusCode;
            Code = code;
            Details = details;
        }

        public HttpStatusCode StatusCode { get; }
        public string? Code { get; }
        public JsonNode? Details { get; }
    }

    internal sealed record Envelope(
        bool HasBody,
        bool Success,
        JsonNode? Data,
        string Error,
        string? Code,
        JsonNode? Details);

    internal static Envelope ExtractEnvelope(JsonNode? node, HttpResponseMessage response)
    {
        if (node is null)
        {
            var statusSuccess = response.IsSuccessStatusCode;
            return new Envelope(
                HasBody: false,
                Success: statusSuccess,
                Data: null,
                Error: response.ReasonPhrase ?? "Request failed",
                Code: statusSuccess ? null : $"http-{(int)response.StatusCode}",
                Details: null);
        }

        var success = node["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode;
        var data = node["data"];
        var error = node["error"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Request failed";
        var rawCode = node["code"]?.GetValue<string>();
        var code = success || !string.IsNullOrWhiteSpace(rawCode)
            ? rawCode
            : $"http-{(int)response.StatusCode}";
        return new Envelope(HasBody: true, Success: success, Data: data, Error: error, Code: code, Details: node["details"]);
    }

    internal static int FailureExitCode(HttpResponseMessage response) =>
        FailureExitCode(response.StatusCode);

    internal static int FailureExitCode(HttpStatusCode statusCode) => 1;

    private void WriteEnvelopeFailure(Envelope envelope)
    {
        var details = envelope.Details is null ? string.Empty : $" details={envelope.Details.ToJsonString()}";
        _err.WriteLine($"{envelope.Error} (code={envelope.Code}){details}");
    }

    internal async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        bool printServerUnavailable = true,
        IReadOnlyDictionary<string, string>? headers = null,
        int retries = 0)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, path);
                if (body is not null)
                    request.Content = JsonContent.Create(body, options: JsonOptions);
                if (headers is not null)
                {
                    foreach (var (name, value) in headers)
                        request.Headers.TryAddWithoutValidation(name, value);
                }
                return await _http.SendAsync(request);
            }
            catch (HttpRequestException) when (attempt < retries)
            {
            }
            catch (HttpRequestException) when (printServerUnavailable)
            {
                _err.WriteLine(ServerUnavailableMessage);
                return null;
            }
        }
    }
}
