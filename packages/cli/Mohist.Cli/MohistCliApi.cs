using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed class MohistCliApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };
    internal static JsonSerializerOptions JsonOutputOptions => JsonOptions;

    private readonly HttpClient _http;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly IFileSystem _fileSystem;
    private readonly ICommandExecutor _commandExecutor;
    private readonly TextReader _standardInput;
    private readonly Func<string> _getUserHome;

    internal TextWriter Output => _out;
    internal TextWriter Error => _err;
    internal IFileSystem FileSystem => _fileSystem;
    internal ICommandExecutor CommandExecutor => _commandExecutor;
    internal TextReader StandardInput => _standardInput;
    internal HttpClient Http => _http;

    public MohistCliApi(
        HttpClient http,
        TextWriter output,
        TextWriter error,
        IFileSystem fileSystem,
        ICommandExecutor commandExecutor,
        TextReader? standardInput = null,
        Func<string>? getUserHome = null)
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
    }

    public async Task<int> PrintGetAsync(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path, body: null);
        return response is null ? 1 : await PrintResponseAsync(response);
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
            _err.WriteLine(envelope.Code is null ? envelope.Error : $"{envelope.Error} ({envelope.Code})");
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
        using var response = await SendAsync(HttpMethod.Put, path, body);
        return response is null ? 1 : await PrintEnvelopeAsync(response, mode, tableShape);
    }

    /// <summary>
    /// Expands an <c>@file</c> reference into the file's UTF-8 contents.
    /// Values that do not start with <c>@</c> are returned as-is (inline).
    /// Read errors print to <see cref="Error"/> and return
    /// <see cref="Result.Failure"/>; the caller exits non-zero.
    /// </summary>
    public abstract record ExpandAtFileResult
    {
        private ExpandAtFileResult() { }

        public sealed record Success(string Value) : ExpandAtFileResult;

        public sealed record Failure(string Message) : ExpandAtFileResult;
    }

    public async Task<ExpandAtFileResult> ExpandAtFileAsync(string? raw, string optionName)
    {
        if (string.IsNullOrEmpty(raw))
            return new ExpandAtFileResult.Success(string.Empty);

        if (!raw.StartsWith('@'))
            return new ExpandAtFileResult.Success(raw);

        var path = raw[1..];
        if (string.IsNullOrWhiteSpace(path))
        {
            await _err.WriteLineAsync(
                $"{optionName}: '@' must be followed by a file path").ConfigureAwait(false);
            return new ExpandAtFileResult.Failure($"{optionName}: missing file path after '@'");
        }

        try
        {
            var text = await _fileSystem.ReadAllTextAsync(path).ConfigureAwait(false);
            return new ExpandAtFileResult.Success(text ?? string.Empty);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync(
                $"{optionName}: could not read file '{path}' ({ex.Message})").ConfigureAwait(false);
            return new ExpandAtFileResult.Failure($"{optionName}: could not read file '{path}'");
        }
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
            _err.WriteLine(ex.Code is null ? ex.Message : $"{ex.Message} ({ex.Code})");
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
            _err.WriteLine(ex.Code is null ? ex.Message : $"{ex.Message} ({ex.Code})");
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

        if (string.Equals(mode, "json", StringComparison.Ordinal))
        {
            _out.WriteLine(filtered.ToJsonString(JsonOptions));
            return 0;
        }

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
            return 4;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);

        var envelope = ExtractEnvelope(node, response);
        if (!envelope.Success)
        {
            _err.WriteLine(envelope.Code is null ? envelope.Error : $"{envelope.Error} ({envelope.Code})");
            return FailureExitCode(response);
        }

        var runner = envelope.Data?["runner"] as JsonObject;
        if (runner is null)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }

        if (string.Equals(mode, "json", StringComparison.Ordinal))
        {
            _out.WriteLine(runner.ToJsonString(JsonOptions));
            return 0;
        }

        RenderRunnerShow(runner);
        return 0;
    }

    public async Task<int> PrintRunnerStatusAsync(string projectId, string mode)
    {
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
            _err.WriteLine(ex.Code is null ? ex.Message : $"{ex.Message} ({ex.Code})");
            return FailureExitCode(ex.StatusCode);
        }

        if (data is null)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }

        var runners = data["runners"] as JsonArray ?? new JsonArray();

        if (string.Equals(mode, "json", StringComparison.Ordinal))
        {
            _out.WriteLine(data.ToJsonString(JsonOptions));
            return 0;
        }

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

    public async Task<int> PrintOpencodeModelsAsync(string projectId, string mode)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _err.WriteLine(MohistCliCommands.NoActiveProjectMessage);
            return 1;
        }

        JsonNode? data;
        try
        {
            data = await GetDataAsync($"/api/projects/{Uri.EscapeDataString(projectId)}/opencode/models");
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }
        catch (ApiResponseException ex)
        {
            _err.WriteLine(ex.Code is null ? ex.Message : $"{ex.Message} ({ex.Code})");
            return FailureExitCode(ex.StatusCode);
        }

        if (data is null)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }

        if (string.Equals(mode, "json", StringComparison.Ordinal))
        {
            _out.WriteLine(data.ToJsonString(JsonOptions));
            return 0;
        }

        var models = data["models"] as JsonArray ?? new JsonArray();
        foreach (var item in models)
        {
            var id = item?.GetValue<string>();
            if (!string.IsNullOrEmpty(id))
                _out.WriteLine(id);
        }
        return 0;
    }

    public async Task<int> PrintSystemInfoAsync(string mode)
    {
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
            _err.WriteLine(envelope.Code is null ? envelope.Error : $"{envelope.Error} ({envelope.Code})");
            return FailureExitCode(response);
        }

        var data = envelope.Data;

        if (string.Equals(mode, "json", StringComparison.Ordinal))
        {
            _out.WriteLine(data is null ? "null" : data.ToJsonString(JsonOptions));
            return 0;
        }

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

        _err.WriteLine("Server is not running. Start with: mo server start");

        if (string.Equals(mode, "json", StringComparison.Ordinal))
        {
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
            _out.WriteLine(payload.ToJsonString(JsonOptions));
            await Task.CompletedTask;
            return;
        }

        _out.WriteLine("Server diagnostics unavailable (server not reachable).");
        _out.WriteLine();
        _out.WriteLine("CLI (local)");
        WriteKeyValue("  version", cliVersion ?? "<unknown>");
        await Task.CompletedTask;
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

    internal const string ServerUnavailableMessage = "Server is not running. Start with: mo server start";
    internal const string RunnerStartHint = "npx mohist runner";

    public abstract record OutputModeResult
    {
        private OutputModeResult() { }

        public sealed record Valid(string Mode) : OutputModeResult;

        public sealed record Invalid(string Message) : OutputModeResult;
    }

    public static OutputModeResult ValidateOutputMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || string.Equals(mode, "json", StringComparison.Ordinal))
            return new OutputModeResult.Valid("json");

        if (string.Equals(mode, "table", StringComparison.Ordinal))
            return new OutputModeResult.Valid("table");

        return new OutputModeResult.Invalid(
            $"--output must be 'table' or 'json' (got '{mode}')");
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

    public async Task<(string ProjectId, int Exit)> ResolveProject(string? project, string? projectId)
    {
        var resolved = await ResolveProjectIdAsync(project, projectId);
        if (resolved is null)
            return ("", 1);
        return (resolved, 0);
    }

    public async Task<int> PrintWithOutputAsync(
        string path,
        string mode,
        string? tableShape = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var response = await SendAsync(HttpMethod.Get, path, body: null, headers: headers);
        return response is null ? 1 : await PrintEnvelopeAsync(response, mode, tableShape);
    }

    public async Task<int> PrintPostWithOutputAsync(
        string path,
        object body,
        string mode,
        string? tableShape = null,
        bool rawJson = false,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, headers: headers);
        return response is null ? 1 : await PrintEnvelopeAsync(response, mode, tableShape, rawJson: rawJson);
    }

    public async Task<int> PrintPatchWithOutputAsync(string path, object body, string mode, string? tableShape = null)
    {
        using var response = await SendAsync(HttpMethod.Patch, path, body);
        return response is null ? 1 : await PrintEnvelopeAsync(response, mode, tableShape);
    }

    public async Task<int> PrintDeleteWithOutputAsync(string path, string mode, string? tableShape = null, JsonNode? successDataFallback = null)
    {
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
        var shape = ParseTableShape(tableShape);
        return await RenderTableAsync(data, shape);
    }

    public enum TableShape
    {
        ProjectList,
        ProjectShow,
        IssueList,
        IssueShow,
        WorkflowStatus,
        Sessions,
        RepoList,
        FeedbackList,
        FeedbackShow,
        AgentList,
        AgentShow,
        EpicList,
        EpicShow,
        EpicLink,
        EpicUnlink,
        LabelList,
        IssueTemplateList,
        IssueTemplateShow,
        RunnerList,
        WorkflowProfile,
        WorkflowVariables,
        WorkflowProfilePrompt,
        WorkflowProfilePreview,
        SessionMetadata,
        SessionTranscriptSummary,
        SessionRecovery,
        AgentSessionLaunch,
        AgentSessionFollowup,
        AgentSessionCancel,
        AgentSessionList,
        AgentSessionShow,
        AgentSessionTranscript,
        RoutingRuleList,
        RoutingRule,
        ProjectTemplateList,
        ProjectTemplateShow,
        ProjectWorkflowProfile,
        IssueArchiveCompleted,
        WorkflowRunDetail,
        WorkflowRunVariables,
        WorkflowRunEvents,
        DeadLetterList,
        DeadLetterRedelivery,
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

    public async Task<JsonNode?> PostDataAsync(string path, object body)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, printServerUnavailable: false);
        return await ReadSuccessDataAsync(response!);
    }

    public async Task<int> PrintWorkflowProfilesDescribedAsync(string? projectId = null)
    {
        var path = projectId is not null
            ? $"/api/workflow-profiles?project={Uri.EscapeDataString(projectId)}"
            : "/api/workflow-profiles";
        try
        {
            var data = await GetDataAsync(path);
            RenderWorkflowProfilesDescribed(data);
            return 0;
        }
        catch (ApiResponseException ex)
        {
            _err.WriteLine(ex.Code is null ? ex.Message : $"{ex.Message} ({ex.Code})");
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
            await _fileSystem.WriteAllTextAsync(ProjectStatePath, state.ToJsonString(JsonOptions));
            _out.WriteLine($"Active project: {name ?? id} ({id})");
            return 0;
        }
        catch (ApiResponseException ex)
        {
            _err.WriteLine(ex.Code is null ? ex.Message : $"{ex.Message} ({ex.Code})");
            return FailureExitCode(ex.StatusCode);
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }
    }

    public async Task<string?> ResolveProjectIdAsync(string? project, string? projectId)
    {
        var hasProject = !string.IsNullOrWhiteSpace(project);
        var hasProjectId = !string.IsNullOrWhiteSpace(projectId);

        if (hasProject && hasProjectId)
        {
            if (string.Equals(project, projectId, StringComparison.Ordinal))
                return project!;

            _err.WriteLine(
                $"--project and --project-id resolve to different values ('{project}' vs '{projectId}'). " +
                "Pass only one of the two options, or pass matching values.");
            return null;
        }

        if (hasProject)
            return project!;

        if (hasProjectId)
            return projectId!;

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

    public Task<string?> ResolveProjectIdAsync(string? projectId) =>
        ResolveProjectIdAsync(null, projectId);

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

        throw new ApiResponseException(response.StatusCode, envelope.Error, envelope.Code);
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
            _err.WriteLine(envelope.Code is null ? envelope.Error : $"{envelope.Error} ({envelope.Code})");
            return FailureExitCode(response);
        }

        var data = envelope.Data ?? successDataFallback;
        _out.WriteLine(data is null ? "OK" : data.ToJsonString(JsonOptions));
        return 0;
    }

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
            _err.WriteLine(envelope.Code is null ? envelope.Error : $"{envelope.Error} ({envelope.Code})");
            return FailureExitCode(response);
        }

        _out.WriteLine(node.ToJsonString(JsonOptions));
        return 0;
    }

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
            _err.WriteLine(envelope.Code is null ? envelope.Error : $"{envelope.Error} ({envelope.Code})");
            return new PostResult(FailureExitCode(response), null, envelope.Error, envelope.Code);
        }

        var data = envelope.Data;
        _out.WriteLine(data is null ? "OK" : data.ToJsonString(JsonOptions));
        return new PostResult(0, data, null, null);
    }

    private sealed class ApiResponseException : Exception
    {
        public ApiResponseException(HttpStatusCode statusCode, string message, string? code = null) : base(message)
        {
            StatusCode = statusCode;
            Code = code;
        }

        public HttpStatusCode StatusCode { get; }
        public string? Code { get; }
    }

    internal sealed record Envelope(
        bool HasBody,
        bool Success,
        JsonNode? Data,
        string Error,
        string? Code);

    internal static Envelope ExtractEnvelope(JsonNode? node, HttpResponseMessage response)
    {
        if (node is null)
        {
            return new Envelope(
                HasBody: false,
                Success: response.IsSuccessStatusCode,
                Data: null,
                Error: response.ReasonPhrase ?? "Request failed",
                Code: null);
        }

        var success = node["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode;
        var data = node["data"];
        var error = node["error"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Request failed";
        var code = node["code"]?.GetValue<string>();
        return new Envelope(HasBody: true, Success: success, Data: data, Error: error, Code: code);
    }

    internal static int FailureExitCode(HttpResponseMessage response) =>
        FailureExitCode(response.StatusCode);

    internal static int FailureExitCode(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.NotFound ? 4 : 1;

    internal async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        bool printServerUnavailable = true,
        IReadOnlyDictionary<string, string>? headers = null)
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
        catch (HttpRequestException) when (printServerUnavailable)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return null;
        }
    }
}
