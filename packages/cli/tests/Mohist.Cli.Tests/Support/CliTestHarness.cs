namespace Mohist.Cli.Tests.Support;

public static class CliTestHarness
{
    public const string BaseAddress = "http://localhost:3456";

    public static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        Create(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder = null,
            string? activeProjectId = "proj_abc")
    {
        var handler = new RecordingHttpHandler(responder ?? DefaultResponse);
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseAddress) };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        if (activeProjectId is not null)
        {
            fs.AddFile(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
                $"{{\"activeProjectId\":\"{activeProjectId}\"}}");
        }
        return (handler, http, output, error, fs, new FakeCommandExecutor());
    }

    public static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateSync(
            Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
            string? activeProjectId = "proj_abc")
    {
        return Create(
            (req, _) =>
            {
                var response = responder?.Invoke(req);
                return Task.FromResult(response ?? RecordingHttpHandler.Json(new { success = true, data = new { } }));
            },
            activeProjectId);
    }

    private static Task<HttpResponseMessage> DefaultResponse(HttpRequestMessage _, CancellationToken __)
        => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
}
