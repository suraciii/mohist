using System.Net;
using System.Text;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Project.Api;

public class NoActiveProjectMessageSpecs
{
    private const string ExpectedMessage =
        "Run 'mo project use <name-or-id>' or pass --project <name-or-id>";

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void NoActiveProjectMessage_HasExpectedWording()
    {
        Assert.Equal(ExpectedMessage, MohistCliCommands.NoActiveProjectMessage);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void NoActiveProjectMessage_DoesNotMentionProjectId()
    {
        Assert.DoesNotContain("--project-id", MohistCliCommands.NoActiveProjectMessage);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void NoActiveProjectMessage_MentionsBothRemediationOptions()
    {
        Assert.Contains("mo project use <name-or-id>", MohistCliCommands.NoActiveProjectMessage);
        Assert.Contains("--project <name-or-id>", MohistCliCommands.NoActiveProjectMessage);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ResolveProjectIdAsync_NoOptionsAndNoActiveProject_EmitsHelperMessage()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync(null, null);

        Assert.Null(resolved);
        var err = error.ToString().TrimEnd('\r', '\n');
        Assert.Equal(ExpectedMessage, err);
        Assert.Empty(http.Requests);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ResolveProjectIdAsync_BlankOptionsAndNoActiveProject_EmitsHelperMessage()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync("", " ");

        Assert.Null(resolved);
        var err = error.ToString().TrimEnd('\r', '\n');
        Assert.Equal(ExpectedMessage, err);
        Assert.Empty(http.Requests);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ResolveProjectIdAsync_BlankActiveProject_EmitsHelperMessage()
    {
        var files = new FakeFileSystem();
        var statePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "cli-state.json");
        files.AddDirectory(Path.GetDirectoryName(statePath)!);
        await files.WriteAllTextAsync(statePath, """{ "activeProjectId": "" }""");
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync(null, null);

        Assert.Null(resolved);
        var err = error.ToString().TrimEnd('\r', '\n');
        Assert.Equal(ExpectedMessage, err);
        Assert.Empty(http.Requests);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ResolveProjectIdAsync_CorruptStateFile_EmitsHelperMessage()
    {
        var files = new FakeFileSystem();
        var statePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "cli-state.json");
        files.AddDirectory(Path.GetDirectoryName(statePath)!);
        await files.WriteAllTextAsync(statePath, "not-json");
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync(null, null);

        Assert.Null(resolved);
        var err = error.ToString().TrimEnd('\r', '\n');
        Assert.Equal(ExpectedMessage, err);
        Assert.Empty(http.Requests);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task IssueShow_NoProjectAndNoActiveProject_DiagnosticMatchesHelper()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "show", "83"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        var err = error.ToString().TrimEnd('\r', '\n');
        Assert.Equal(ExpectedMessage, err);
        Assert.Empty(http.Requests);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task IssueList_NoProjectAndNoActiveProject_DiagnosticMatchesHelper()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "list"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        var err = error.ToString().TrimEnd('\r', '\n');
        Assert.Equal(ExpectedMessage, err);
        Assert.Empty(http.Requests);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task IssueSessions_NoProjectAndNoActiveProject_DiagnosticMatchesHelper()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "sessions", "83"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        var err = error.ToString().TrimEnd('\r', '\n');
        Assert.Equal(ExpectedMessage, err);
        Assert.Empty(http.Requests);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void MohistCliCommands_IsTheSingleSourceOfTruthForTheDiagnostic()
    {
        var source = File.ReadAllText(GetCliCommandsPath());
        Assert.Contains("internal const string NoActiveProjectMessage", source);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void MohistCliApi_ReferencesTheHelperForTheDiagnostic()
    {
        var source = File.ReadAllText(GetCliApiPath());
        var occurrences = CountOccurrences(source, "Run 'mo project use <name-or-id>' or pass --project <name-or-id>");
        Assert.Equal(0, occurrences);
        Assert.Contains("MohistCliCommands.NoActiveProjectMessage", source);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void IssueCommandsFile_ReferencesTheHelperForTheDiagnostic()
    {
        var source = File.ReadAllText(GetIssueCommandsPath());
        var occurrences = CountOccurrences(source, "Run 'mo project use <name-or-id>' or pass --project <name-or-id>");
        Assert.Equal(0, occurrences);
        Assert.Contains("MohistCliCommands.NoActiveProjectMessage", source);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
            return 0;
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string GetCliCommandsPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "..", "cli", "Mohist.Cli", "MohistCliCommands.cs"));
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"Could not find MohistCliCommands.cs at {candidate}");
        }
        return candidate;
    }

    private static string GetCliApiPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "..", "cli", "Mohist.Cli", "MohistCliApi.cs"));
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"Could not find MohistCliApi.cs at {candidate}");
        }
        return candidate;
    }

    private static string GetIssueCommandsPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "..", "cli", "Mohist.Cli", "MohistCliCommands.Issue.cs"));
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"Could not find MohistCliCommands.Issue.cs at {candidate}");
        }
        return candidate;
    }

    private static MohistCliApi CreateApi(RecordingHttpHandler http, StringWriter output, StringWriter error, IFileSystem files) =>
        new(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            error,
            files,
            new NoopCommandExecutor());

    private sealed class NoopCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((0, "", ""));
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void EnqueueJson(HttpStatusCode status, string json)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "success": true, "data": null }""", Encoding.UTF8, "application/json"),
                });
        }
    }
}
