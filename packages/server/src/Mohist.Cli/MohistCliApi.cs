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

    internal TextWriter Output => _out;
    internal TextWriter Error => _err;
    internal IFileSystem FileSystem => _fileSystem;

    public MohistCliApi() : this(new HttpClient
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("MOHIST_SERVER_URL") ?? "http://localhost:3456"),
        Timeout = TimeSpan.FromSeconds(30),
    }, Console.Out, Console.Error)
    {
    }

    public MohistCliApi(HttpClient http, TextWriter output, TextWriter error, IFileSystem? fileSystem = null)
    {
        _http = http;
        _out = output;
        _err = error;
        _fileSystem = fileSystem ?? RealFileSystem.Instance;
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
}
