using System.Net;
using System.Text;
using System.Text.Json;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

internal sealed class UpdateTestFactory
{
    public const string UnitDir = "/units";
    public const string ServerAddress = "http://localhost:3456";

    public UpdateTestFactory(string? root = null)
    {
        Root = root;
        Files = new FakeFileSystem();
        Commands = new FakeCommandExecutor();
        Stdout = new StringWriter();
        Stderr = new StringWriter();
        Installer = new SystemdServiceInstaller(Stdout, Stderr, Files, Commands);
    }

    public string? Root { get; }
    public FakeFileSystem Files { get; }
    public FakeCommandExecutor Commands { get; }
    public StringWriter Stdout { get; }
    public StringWriter Stderr { get; }
    public SystemdServiceInstaller Installer { get; }

    public void SeedPackagedSkillAssets()
    {
        WritePackagedSkillAssets(Files, Path.Combine(RequireRoot(), ".publish", "cli", "skill-data"));
        // `mo update` now syncs presets next to skill-data; seed a valid preset
        // bundle in the publish dir so the preset sync step succeeds.
        var presetsRoot = Path.Combine(RequireRoot(), ".publish", "cli", "presets");
        Files.AddDirectory(presetsRoot);
        Files.AddFile(Path.Combine(presetsRoot, "manifest.json"), "{\"supervisor\":{\"rules\":[]}}");
    }

    public void SeedManagedSkillAssets() =>
        WritePackagedSkillAssets(Files, Path.Combine(RequireRoot(), ".mohist", "cli", "skill-data"));

    public void SeedRunnerUnit()
    {
        Files.AddDirectory(UnitDir);
        Files.AddFile(
            Path.Combine(UnitDir, "mohist-runner.service"),
            "[Unit]\nDescription=Mohist Runner\n\n[Service]\nExecStart=node packages/runner/dist/cli.js\n\n[Install]\nWantedBy=default.target\n");
    }

    public void AssertManagedSkillAssetsSynced()
    {
        var tempRoot = RequireRoot();
        var managedRoot = Path.Combine(tempRoot, ".mohist", "cli", "skill-data");
        Assert.True(Files.HasFile(Path.Combine(managedRoot, "mohist", "SKILL.md")), "Expected mohist SKILL.md");
        Assert.True(Files.HasFile(Path.Combine(managedRoot, "mohist-explore", "SKILL.md")), "Expected mohist-explore SKILL.md");
        var mohistSkillsDir = Path.Combine(tempRoot, ".mohist", "skills");
        Assert.False(Files.DirectoryExists(mohistSkillsDir), "Internal .mohist/skills should remain untouched by sync");
    }

    public HttpClient BuildHttp(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri(ServerAddress),
    };

    public SourceCodeUpdater BuildUpdater(
        HttpMessageHandler? httpHandler = null,
        bool withEnvironment = true,
        TimeSpan? serverReadyTimeout = null,
        string? userHome = null,
        TimeSpan? runnerIdentityTimeout = null,
        TimeSpan? runnerIdentityPollInterval = null,
        Func<string?>? getLocalHostname = null,
        string? unitDir = null,
        TimeProvider? timeProvider = null,
        bool managedUpdatesEnabled = false)
    {
        var home = userHome ?? Root;
        return SourceCodeUpdater.CreateWithDefaults(
            Stdout,
            Stderr,
            Installer,
            Commands,
            Files,
            withEnvironment ? new MockEnvironmentVariableProvider() : null,
            httpHandler is null ? null : BuildHttp(httpHandler),
            serverReadyTimeout,
            getUserHome: home is null ? null : () => home,
            runnerIdentityTimeout: runnerIdentityTimeout,
            runnerIdentityPollInterval: runnerIdentityPollInterval,
            getLocalHostname: getLocalHostname,
            unitDir: unitDir,
            timeProvider: timeProvider,
            managedUpdatesEnabled: managedUpdatesEnabled);
    }

    public static string HealthySystemInfoJson(string runningGitHash = "abc123", string runnerStatus = "active")
    {
        return $"{{\"success\":true,\"data\":{{\"running\":{{\"gitHash\":\"{runningGitHash}\"}},\"services\":{{\"runner\":\"{runnerStatus}\"}}}}}}";
    }

    public static string BuildRunnerIdentityResponse(string runnerId, string hostname, string? buildGitHash, string status)
    {
        var hash = buildGitHash is null ? "null" : $"\"{buildGitHash}\"";
        return $"{{\"success\":true,\"data\":{{\"runnerId\":\"{runnerId}\",\"hostname\":\"{hostname}\",\"buildGitHash\":{hash},\"status\":\"{status}\",\"connectionState\":\"connected\"}}}}";
    }

    internal static string ExtractRunningGitHash(string systemInfoJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(systemInfoJson);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("running", out var running)
                && running.TryGetProperty("gitHash", out var gitHash)
                && gitHash.ValueKind == JsonValueKind.String)
            {
                return gitHash.GetString() ?? "unknown";
            }
        }
        catch
        {
        }
        return "unknown";
    }

    private string RequireRoot() =>
        Root ?? throw new InvalidOperationException("This helper requires a factory created with a root path.");

    private static void WritePackagedSkillAssets(FakeFileSystem files, string sourceRoot)
    {
        files.AddDirectory(Path.Combine(sourceRoot, "mohist"));
        files.AddDirectory(Path.Combine(sourceRoot, "mohist-explore"));
        files.AddFile(
            Path.Combine(sourceRoot, "mohist", "SKILL.md"),
            "---\nname: mohist\ndescription: test\n---\n\n# mohist\n");
        files.AddFile(
            Path.Combine(sourceRoot, "mohist-explore", "SKILL.md"),
            "---\nname: mohist-explore\ndescription: test\n---\n\n# mohist-explore\n");
    }
}

internal sealed class FakeCommandExecutor : ICommandExecutor
{
    public readonly List<(string FileName, string[] Args, string? WorkingDirectory)> ExecutedCommands = new();
    private readonly Queue<int> _exitCodes = new();
    private readonly Queue<string> _stdout = new();
    private readonly Queue<string> _stderr = new();
    private readonly List<(string FileName, Func<string[], bool> Match, int ExitCode)> _exitCodeRules = new();
    private readonly List<(string FileName, Func<string[], bool> Match, string Stdout)> _stdoutRules = new();
    private readonly List<(string FileName, Func<string[], bool> Match, int ExitCode, string Stdout, string Stderr)> _resultRules = new();

    public Action<string, string[]>? OnExecute { get; set; }

    public void SetNextExitCode(int code) => _exitCodes.Enqueue(code);
    public void SetNextStdout(string stdout) => _stdout.Enqueue(stdout);
    public void SetNextResult(int exitCode, string stdout, string stderr)
    {
        _exitCodes.Enqueue(exitCode);
        _stdout.Enqueue(stdout);
        _stderr.Enqueue(stderr);
    }
    public void SetExitCodeFor(string fileName, Func<string[], bool> match, int code) => _exitCodeRules.Add((fileName, match, code));
    public void SetStdoutFor(string fileName, Func<string[], bool> match, string stdout) => _stdoutRules.Add((fileName, match, stdout));
    public void SetResultFor(string fileName, Func<string[], bool> match, int exitCode, string stdout, string stderr)
        => _resultRules.Add((fileName, match, exitCode, stdout, stderr));

    public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        ExecutedCommands.Add((fileName, args, workingDirectory));
        OnExecute?.Invoke(fileName, args);
        var resultRule = _resultRules.FirstOrDefault(rule => rule.FileName == fileName && rule.Match(args));
        if (resultRule.Match is not null)
            return Task.FromResult((resultRule.ExitCode, resultRule.Stdout, resultRule.Stderr));
        var rule = _exitCodeRules.FirstOrDefault(rule => rule.FileName == fileName && rule.Match(args));
        var code = rule.Match is not null ? rule.ExitCode : _exitCodes.Count > 0 ? _exitCodes.Dequeue() : 0;
        var stdoutRule = _stdoutRules.FirstOrDefault(rule => rule.FileName == fileName && rule.Match(args));
        var stdout = stdoutRule.Match is not null ? stdoutRule.Stdout : _stdout.Count > 0 ? _stdout.Dequeue() : "";
        var stderr = _stderr.Count > 0 ? _stderr.Dequeue() : "";
        return Task.FromResult((code, stdout, stderr));
    }
}

internal sealed class SequenceHttpHandler : HttpMessageHandler
{
    private const string DefaultSystemInfoJson =
        "{\"success\":true,\"data\":{\"running\":{\"gitHash\":\"testsha\"},\"services\":{\"runner\":\"active\"}}}";

    private readonly ResponseSpec?[] _responses;
    private string? _systemInfoJson;

    public int Requests { get; private set; }
    public List<string> Paths { get; } = new();

    public SequenceHttpHandler(params HttpStatusCode?[] statuses)
        : this(ExpandStatusResponses(statuses))
    {
    }

    public SequenceHttpHandler(params ResponseSpec?[] responses)
        : this(responses, systemInfoJson: null)
    {
    }

    public SequenceHttpHandler(ResponseSpec?[] responses, string? systemInfoJson)
    {
        _responses = responses.Length == 0 ? [new ResponseSpec(HttpStatusCode.OK)] : responses;
        _systemInfoJson = systemInfoJson;
    }

    public static SequenceHttpHandler WithSystemInfo(string? systemInfoJson, params ResponseSpec?[] responses)
    {
        return new SequenceHttpHandler(responses, systemInfoJson);
    }

    public void SetSystemInfoJson(string? json)
    {
        _systemInfoJson = json;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.PathAndQuery ?? "";
        Paths.Add(path);

        if (string.Equals(path, "/api/system/info", StringComparison.Ordinal))
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_systemInfoJson ?? DefaultSystemInfoJson)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                }
            });
        }

        if (_systemInfoJson is not null && path.StartsWith("/api/runner/identity", StringComparison.Ordinal))
        {
            Requests++;
            var runnerHash = UpdateTestFactory.ExtractRunningGitHash(_systemInfoJson);
            var identityJson = $"{{\"success\":true,\"data\":{{\"buildGitHash\":\"{runnerHash}\"}}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(identityJson)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                }
            });
        }

        var index = Math.Min(Requests, _responses.Length - 1);
        Requests++;
        var response = _responses[index];
        if (response is null)
            throw new HttpRequestException("server not ready");

        var message = new HttpResponseMessage(response.StatusCode);
        if (response.Body is not null)
        {
            message.Content = new StringContent(response.Body);
            if (response.ContentType is not null)
                message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(response.ContentType);
        }
        else if (response.Content is not null)
        {
            message.Content = response.Content;
            if (response.ContentType is not null)
                message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(response.ContentType);
        }
        else if (string.Equals(path, "/", StringComparison.Ordinal))
        {
            // Default to healthy HTML for unknown calls to /.
            message.Content = new StringContent("<html><script src=\"/assets/app.js\"></script></html>")
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") }
            };
        }
        else if (path.StartsWith("/assets/", StringComparison.Ordinal))
        {
            message.Content = new StringContent("// asset body");
        }

        return Task.FromResult(message);
    }

    private static ResponseSpec?[] ExpandStatusResponses(HttpStatusCode?[] statuses)
    {
        if (statuses.Length == 0)
            statuses = [HttpStatusCode.OK];

        var expanded = new List<ResponseSpec?>();
        foreach (var response in statuses)
        {
            if (response is null)
            {
                expanded.Add(null);
                continue;
            }

            if (response.Value == HttpStatusCode.OK)
            {
                expanded.Add(new ResponseSpec(HttpStatusCode.OK));
                expanded.Add(new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"));
                expanded.Add(new ResponseSpec(HttpStatusCode.OK));
                continue;
            }

            expanded.Add(new ResponseSpec(response.Value));
        }

        return expanded.ToArray();
    }
}

internal sealed record ResponseSpec(
    HttpStatusCode StatusCode,
    string? Body = null,
    string? ContentType = null,
    HttpContent? Content = null);

internal sealed class NeverCompletingContent : HttpContent
{
    private readonly TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => _pending.Task;

    protected override bool TryComputeLength(out long length)
    {
        length = 1024 * 1024;
        return true;
    }
}

internal sealed class OutcomeCapturingHttpHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _systemInfoJson;

    public OutcomeCapturingHttpHandler(string systemInfoJson)
    {
        _systemInfoJson = systemInfoJson;
    }

    public HttpStatusCode OutcomeResponseStatusCode { get; set; } = HttpStatusCode.OK;

    public CliOutcomeRequestPayload? LastOutcomeRequest { get; private set; }
    public List<string> Paths { get; } = new();

    public string BuildStatusResponseJson()
    {
        if (LastOutcomeRequest is null)
            throw new InvalidOperationException("No outcome request captured");

        var payload = LastOutcomeRequest;
        var response = new
        {
            jobId = payload.JobId,
            status = payload.Status,
            stage = payload.Stage,
            outcome = payload.Outcome,
            unavailableCapability = payload.UnavailableCapability,
            runningGitHash = payload.SourceHead,
            sourceHead = payload.SourceHead,
            updateAvailable = false,
            sourcePath = (string?)null,
            serverUnit = (string?)null,
            runnerUnit = (string?)null,
            reason = (string?)null,
            logs = (payload.Logs is null ? new List<CliOutcomeLogPayload>() : payload.Logs).Select(l => new
            {
                at = l.At,
                stage = l.Stage,
                message = l.Message,
            }),
            createdAt = TestTime.UtcNow,
            updatedAt = TestTime.UtcNow,
            completedAt = TestTime.UtcNow,
        };
        return JsonSerializer.Serialize(response, JsonOptions);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.PathAndQuery ?? "";
        Paths.Add(path);

        if (string.Equals(path, "/api/system/info", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_systemInfoJson)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                }
            };
        }

        if (path.StartsWith("/api/runner/identity", StringComparison.Ordinal))
        {
            var runnerHash = UpdateTestFactory.ExtractRunningGitHash(_systemInfoJson);
            var identityJson = $"{{\"success\":true,\"data\":{{\"buildGitHash\":\"{runnerHash}\"}}}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(identityJson)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                }
            };
        }

        if (string.Equals(path, "/api/system/update/outcome", StringComparison.Ordinal)
            && request.Method == HttpMethod.Post)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            LastOutcomeRequest = JsonSerializer.Deserialize<CliOutcomeRequestPayload>(body, JsonOptions);
            return new HttpResponseMessage(OutcomeResponseStatusCode)
            {
                Content = new StringContent("{\"job\":{}}")
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                }
            };
        }

        if (string.Equals(path, "/api/health", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        if (string.Equals(path, "/", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><script src=\"/assets/app.js\"></script></html>")
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") }
                }
            };
        }

        if (path.StartsWith("/assets/", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("// asset body")
            };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public sealed class CliOutcomeRequestPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("jobId")]
        public string? JobId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string? Status { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("stage")]
        public string? Stage { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("outcome")]
        public string? Outcome { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("unavailableCapability")]
        public string? UnavailableCapability { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("sourceHead")]
        public string? SourceHead { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("logs")]
        public List<CliOutcomeLogPayload>? Logs { get; set; }
    }

    public sealed class CliOutcomeLogPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("at")]
        public DateTimeOffset At { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("stage")]
        public string? Stage { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}

internal sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private string _currentDirectory = "/";

    public string Cwd
    {
        get
        {
            lock (_gate)
            {
                return _currentDirectory;
            }
        }
    }

    public void SetCurrentDirectory(string path)
    {
        lock (_gate)
        {
            _currentDirectory = Normalize(path);
        }
    }

    public IReadOnlyDictionary<string, string> Files
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_files, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public IReadOnlyCollection<string> Directories
    {
        get
        {
            lock (_gate)
            {
                return new HashSet<string>(_directories, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void AddFile(string path, string content)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            _files[normalized] = content;
        }
    }

    public void AddDirectory(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            _directories.Add(normalized);
        }
    }

    public string Read(string path)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(Normalize(path), out var content))
                return content;
            throw new FileNotFoundException($"Fake filesystem has no file at '{path}'.");
        }
    }

    public bool HasFile(string path)
    {
        lock (_gate)
        {
            return _files.ContainsKey(Normalize(path));
        }
    }

    public bool HasDirectory(string path)
    {
        lock (_gate)
        {
            return _directories.Contains(Normalize(path));
        }
    }

    public string CurrentDirectory
    {
        get
        {
            lock (_gate)
            {
                return _currentDirectory;
            }
        }
    }

    public bool Exists(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            return _files.ContainsKey(normalized) || _directories.Contains(normalized);
        }
    }

    public bool DirectoryExists(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            if (_directories.Contains(normalized))
                return true;
            return _files.Keys.Any(key => StartsWithDirectory(key, normalized));
        }
    }

    public void CreateDirectory(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            _directories.Add(normalized);
        }
    }

    public void Delete(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            _files.Remove(normalized);
        }
    }

    public void DeleteDirectory(string path)
    {
        var normalized = Normalize(path);
        var prefix = normalized.EndsWith(Path.DirectorySeparatorChar)
            ? normalized
            : normalized + Path.DirectorySeparatorChar;
        lock (_gate)
        {
            foreach (var dir in _directories.Where(d => d == normalized || d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _directories.Remove(dir);
            }
            foreach (var key in _files.Keys.Where(k => StartsWithDirectory(k, normalized)).ToArray())
            {
                _files.Remove(key);
            }
        }
    }

    public void Move(string source, string destination)
    {
        var sourceKey = Normalize(source);
        var destKey = Normalize(destination);
        lock (_gate)
        {
            if (_directories.Contains(sourceKey))
            {
                _directories.Remove(sourceKey);
                _directories.Add(destKey);
            }

            var prefix = sourceKey.EndsWith(Path.DirectorySeparatorChar) ? sourceKey : sourceKey + Path.DirectorySeparatorChar;
            foreach (var key in _files.Keys.Where(k => k == sourceKey || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var relative = key == sourceKey ? string.Empty : key[prefix.Length..];
                var newKey = string.IsNullOrEmpty(relative) ? destKey : destKey + Path.DirectorySeparatorChar + relative;
                var content = _files[key];
                _files.Remove(key);
                _files[newKey] = content;
            }
        }
    }

    public void MoveFile(string source, string destination)
    {
        var sourceKey = Normalize(source);
        var destKey = Normalize(destination);
        lock (_gate)
        {
            if (!_files.TryGetValue(sourceKey, out var content))
                throw new FileNotFoundException($"Fake filesystem has no file at '{source}'.");
            _files.Remove(sourceKey);
            _files[destKey] = content;
        }
    }

    public string ReadAllText(string path) => Read(path);

    public Task<string> ReadAllTextAsync(string path) => Task.FromResult(Read(path));

    public void WriteAllText(string path, string contents)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            _files[normalized] = contents;
        }
    }

    public Task WriteAllTextAsync(string path, string contents)
    {
        WriteAllText(path, contents);
        return Task.CompletedTask;
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        var normalized = Normalize(path);
        var prefix = normalized.EndsWith(Path.DirectorySeparatorChar) ? normalized : normalized + Path.DirectorySeparatorChar;
        string[] snapshot;
        lock (_gate)
        {
            snapshot = _files.Keys
                .Where(key => searchOption == SearchOption.AllDirectories
                    ? key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    : key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                      && !key.Substring(prefix.Length).Contains(Path.DirectorySeparatorChar))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();
        }
        return snapshot;
    }

    public Stream OpenRead(string path) => new MemoryStream(Encoding.UTF8.GetBytes(Read(path)));

    public Stream OpenWrite(string path) => new RecordingStream(this, path);

    private static string Normalize(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static bool StartsWithDirectory(string filePath, string directoryPath)
    {
        var prefix = directoryPath.EndsWith(Path.DirectorySeparatorChar)
            ? directoryPath
            : directoryPath + Path.DirectorySeparatorChar;
        return filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingStream : MemoryStream
    {
        private readonly FakeFileSystem _owner;
        private readonly string _path;

        public RecordingStream(FakeFileSystem owner, string path)
        {
            _owner = owner;
            _path = path;
        }

        public override void Close()
        {
            base.Close();
            var content = Encoding.UTF8.GetString(ToArray());
            _owner.WriteAllText(_path, content);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var content = Encoding.UTF8.GetString(ToArray());
                _owner.WriteAllText(_path, content);
            }
            base.Dispose(disposing);
        }
    }
}
