using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed class MohistCliApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly HttpClient _http;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly IFileSystem _fileSystem;
    private readonly ICommandExecutor _commandExecutor;
    private readonly TextReader _standardInput;

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
        TextReader? standardInput = null)
    {
        _http = http;
        _out = output;
        _err = error;
        _fileSystem = fileSystem;
        _commandExecutor = commandExecutor;
        _standardInput = standardInput ?? Console.In;
    }

    public async Task<int> PrintGetAsync(string path) =>
        await PrintResponseAsync(await _http.GetAsync(path));

    public async Task<int> PrintProjectListAsync()
    {
        using var response = await _http.GetAsync("/api/projects");
        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);
        if (node is null)
        {
            _out.WriteLine(response.StatusCode);
            return response.IsSuccessStatusCode ? 0 : 1;
        }

        var success = node["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode;
        if (!success)
        {
            var error = node["error"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Request failed";
            var code = node["code"]?.GetValue<string>();
            _err.WriteLine(code is null ? error : $"{error} ({code})");
            return response.StatusCode == HttpStatusCode.NotFound ? 4 : 1;
        }

        var data = node["data"];
        if (data is not JsonArray array || array.Count == 0)
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

    public async Task<int> PrintDeleteAsync(string path) =>
        await PrintResponseAsync(await _http.DeleteAsync(path));

    public async Task<int> PrintPostAsync(string path, object body) =>
        await PrintResponseAsync(await _http.PostAsJsonAsync(path, body, JsonOptions));

    public async Task<PostResult> PostAndReadAsync(string path, object body)
    {
        var response = await _http.PostAsJsonAsync(path, body, JsonOptions);
        return await ReadPostResultAsync(response);
    }

    public async Task<int> PrintPutAsync(string path, object body) =>
        await PrintResponseAsync(await _http.PutAsJsonAsync(path, body, JsonOptions));

    public async Task<int> PrintPatchAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        return await PrintResponseAsync(await _http.SendAsync(request));
    }

    public async Task<JsonNode?> GetDataAsync(string path)
    {
        using var response = await _http.GetAsync(path);
        return await ReadSuccessDataAsync(response);
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

    internal const string ServerUnavailableMessage = "Server is not running. Start with: mo server start";

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

    public async Task<int> PrintWithOutputAsync(string path, string mode, string? tableShape = null)
    {
        try
        {
            using var response = await _http.GetAsync(path);

            if (string.Equals(mode, "json", StringComparison.Ordinal))
                return await PrintResponseAsync(response);

            var data = await ReadSuccessDataAsync(response);
            var shape = ParseTableShape(tableShape);
            return await RenderTableAsync(data, shape);
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }
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
        using var response = await _http.PostAsJsonAsync(path, body, JsonOptions);
        return await ReadSuccessDataAsync(response);
    }

    public async Task<int> PrintWorkflowProfilesDescribedAsync()
    {
        var data = await GetDataAsync("/api/workflow-profiles");
        RenderWorkflowProfilesDescribed(data);
        return 0;
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

            var suitableFor = profile?["suitableFor"];
            if (suitableFor is JsonArray tags && tags.Count > 0)
            {
                var items = tags.Select(t => t?.GetValue<string>() ?? "").Where(s => !string.IsNullOrWhiteSpace(s));
                _out.WriteLine($"  Suitable for: {string.Join(", ", items)}");
            }
            else
            {
                _out.WriteLine("  Suitable for: (not specified)");
            }

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
            return ex.StatusCode == HttpStatusCode.NotFound ? 4 : 1;
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
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
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

        var success = node["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode;
        if (success)
            return node["data"];

        var error = node["error"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Request failed";
        var code = node["code"]?.GetValue<string>();
        throw new ApiResponseException(response.StatusCode, error, code);
    }

    private async Task<int> PrintResponseAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);
        if (node is null)
        {
            _out.WriteLine(response.StatusCode);
            return response.IsSuccessStatusCode ? 0 : 1;
        }

        var success = node["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode;
        if (success)
        {
            var data = node["data"];
            _out.WriteLine(data is null ? "OK" : data.ToJsonString(JsonOptions));
            return 0;
        }

        var error = node["error"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Request failed";
        var code = node["code"]?.GetValue<string>();
        _err.WriteLine(code is null ? error : $"{error} ({code})");
        return response.StatusCode == HttpStatusCode.NotFound ? 4 : 1;
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

        var success = node["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode;
        if (success)
        {
            var data = node["data"];
            _out.WriteLine(data is null ? "OK" : data.ToJsonString(JsonOptions));
            return new PostResult(0, data, null, null);
        }

        var error = node["error"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Request failed";
        var code = node["code"]?.GetValue<string>();
        _err.WriteLine(code is null ? error : $"{error} ({code})");
        return new PostResult(response.StatusCode == HttpStatusCode.NotFound ? 4 : 1, null, error, code);
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
}
