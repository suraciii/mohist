namespace Mohist.Cli.Tests.Support;

public static class CliTestFactory
{
    public const string BaseAddress = "http://localhost:3456";
    public const string UserHome = "/mohist-tests/user";

    public static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        Create(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder = null,
            string? activeProjectId = "proj_abc")
    {
        var (handler, http, output, error, fs, executor, _) = CreateInternal(responder, activeProjectId);
        return (handler, http, output, error, fs, executor);
    }

    public static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateSync(
            Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
            string? activeProjectId = "proj_abc")
    {
        var (handler, http, output, error, fs, executor, _) = CreateInternal(
            (req, _) =>
            {
                var response = responder?.Invoke(req);
                return Task.FromResult(response ?? RecordingHttpHandler.Json(new { success = true, data = new { } }));
            },
            activeProjectId);
        return (handler, http, output, error, fs, executor);
    }

    /// <summary>
    /// Internal overload that also surfaces a <see cref="FakeServiceInstaller"/>
    /// so install/update specs can assert which installer methods (if any)
    /// the dispatched command path invoked. The standard factory methods
    /// omit the installer from their return values.
    /// </summary>
    internal static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor, FakeServiceInstaller Installer)
        CreateInternal(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder = null,
            string? activeProjectId = "proj_abc",
            FakeServiceInstaller? installer = null)
    {
        var handler = new RecordingHttpHandler(responder ?? DefaultResponse);
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseAddress) };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        if (activeProjectId is not null)
        {
            fs.AddFile(
                Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json"),
                $"{{\"activeProjectId\":\"{activeProjectId}\"}}");
        }
        return (handler, http, output, error, fs, new FakeCommandExecutor(), installer ?? new FakeServiceInstaller());
    }

    private static Task<HttpResponseMessage> DefaultResponse(HttpRequestMessage _, CancellationToken __)
        => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
}
