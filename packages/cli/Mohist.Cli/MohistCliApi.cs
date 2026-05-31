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

    internal TextWriter Output => _out;
    internal TextWriter Error => _err;
    internal IFileSystem FileSystem => _fileSystem;
    internal ICommandExecutor CommandExecutor => _commandExecutor;

    public MohistCliApi(
        HttpClient http,
        TextWriter output,
        TextWriter error,
        IFileSystem fileSystem,
        ICommandExecutor commandExecutor)
    {
        _http = http;
        _out = output;
        _err = error;
        _fileSystem = fileSystem;
        _commandExecutor = commandExecutor;
    }

    public async Task<int> PrintGetAsync(string path) =>
        await PrintResponseAsync(await _http.GetAsync(path));

    public async Task<int> PrintDeleteAsync(string path) =>
        await PrintResponseAsync(await _http.DeleteAsync(path));

    public async Task<int> PrintPostAsync(string path, object body) =>
        await PrintResponseAsync(await _http.PostAsJsonAsync(path, body, JsonOptions));

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

    public async Task<JsonNode?> PostDataAsync(string path, object body)
    {
        using var response = await _http.PostAsJsonAsync(path, body, JsonOptions);
        return await ReadSuccessDataAsync(response);
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

    public async Task<string?> ResolveProjectIdAsync(string? explicitProjectId)
    {
        if (!string.IsNullOrWhiteSpace(explicitProjectId))
            return explicitProjectId;

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

    private static string ProjectStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".mohist",
        "cli-state.json");

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
