using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
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
    private const string LegacyRunnerUnit =
        "[Unit]\nDescription=Mohist Runner\n\n[Service]\nExecStart=node packages/runner/dist/cli.js\n\n[Install]\nWantedBy=default.target\n";
    private bool _runnerUnitSeeded;

    public UpdateTestFactory(string? root = null)
    {
        Root = root;
        Files = new FakeFileSystem();
        Commands = new FakeCommandExecutor();
        Runtime = new FakeRuntimeInstallState(Files, UnitDir);
        Leases = new InMemoryRuntimeUpdateLeaseProvider();
        Commands.OnCompleted = Runtime.OnCommandCompleted;
        Stdout = new StringWriter();
        Stderr = new StringWriter();
        Installer = new SystemdServiceInstaller(Stdout, Stderr, Files, Commands);
    }

    public string? Root { get; }
    public FakeFileSystem Files { get; }
    public FakeCommandExecutor Commands { get; }
    public FakeRuntimeInstallState Runtime { get; }
    public InMemoryRuntimeUpdateLeaseProvider Leases { get; }
    public StringWriter Stdout { get; }
    public StringWriter Stderr { get; }
    public SystemdServiceInstaller Installer { get; }

    public void ClearOutput()
    {
        Stdout.GetStringBuilder().Clear();
        Stderr.GetStringBuilder().Clear();
    }

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
        _runnerUnitSeeded = true;
        SeedRunnerUnitAt(Runtime.UnitDir);
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
        IRunnerRuntimeReadinessSignal? runnerReadinessSignal = null,
        IRuntimeUpdateLeaseProvider? runtimeUpdateLeases = null)
    {
        var home = userHome ?? Root;
        Runtime.UnitDir = unitDir ?? (home is null
            ? UnitDir
            : Path.Combine(home, ".config", "systemd", "user"));
        if (_runnerUnitSeeded && !Files.HasFile(Path.Combine(Runtime.UnitDir, "mohist-runner.service")))
            SeedRunnerUnitAt(Runtime.UnitDir);
        var handler = httpHandler ?? new SequenceHttpHandler(HttpStatusCode.OK);
        if (handler is IScopedRuntimeHttpHandler scopedHandler)
            scopedHandler.BindRuntime(Runtime);
        return SourceCodeUpdater.CreateWithDefaults(
            Stdout,
            Stderr,
            Installer,
            Commands,
            Files,
            withEnvironment ? new MockEnvironmentVariableProvider() : null,
            BuildHttp(handler),
            serverReadyTimeout,
            getUserHome: home is null ? null : () => home,
            runnerIdentityTimeout: runnerIdentityTimeout,
            runnerIdentityPollInterval: runnerIdentityPollInterval,
            getLocalHostname: getLocalHostname,
            unitDir: unitDir,
            timeProvider: timeProvider,
            runtimeUpdateLeases: runtimeUpdateLeases ?? Leases,
            runnerReadinessSignal: runnerReadinessSignal ?? Runtime);
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

    private void SeedRunnerUnitAt(string unitDir)
    {
        Files.AddDirectory(unitDir);
        Files.AddFile(Path.Combine(unitDir, "mohist-runner.service"), LegacyRunnerUnit);
    }

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

/// <summary>
/// One fake runtime truth source for update tests. Commands create the same
/// installed files that service restarts consume; HTTP and runner readiness
/// observe that installed state instead of accepting an unrelated JSON claim.
/// </summary>
internal sealed class FakeRuntimeInstallState : IRunnerRuntimeReadinessSignal
{
    private readonly FakeFileSystem _files;
    private readonly object _gate = new();
    private readonly List<RunnerWaiter> _runnerWaiters = [];
    private ServerRuntimeIdentity? _server;
    private ServerRuntimeIdentity? _serverOverride;
    private RunnerRuntimeIdentity? _runner;
    private RunnerRuntimeIdentity? _pendingRunner;
    private string? _runnerServiceStatusOverride;
    private readonly TaskCompletionSource _runnerReadinessWaited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeRuntimeInstallState(FakeFileSystem files, string unitDir)
    {
        _files = files;
        UnitDir = unitDir;
    }

    public string UnitDir { get; set; }
    public Func<RunnerRuntimeIdentity, RunnerRuntimeIdentity>? RunnerIdentityTransform { get; set; }
    public byte[]? RunnerEntryPayload { get; set; }
    public bool HoldRunnerReadiness { get; set; }
    public Task RunnerReadinessWaited => _runnerReadinessWaited.Task;

    public void SetRunnerServiceStatusForReadback(string? status)
    {
        lock (_gate)
        {
            _runnerServiceStatusOverride = status;
        }
    }

    public void ReleaseRunnerReadiness()
    {
        List<RunnerWaiter> waiters;
        RunnerRuntimeIdentity? runtime;
        lock (_gate)
        {
            _runner = _pendingRunner;
            _pendingRunner = null;
            runtime = _runner;
            waiters = _runnerWaiters.ToList();
        }

        if (runtime is null)
            return;
        foreach (var waiter in waiters.Where(waiter => MatchesInstance(runtime, waiter.Expected)))
            waiter.Completion.TrySetResult(runtime);
    }

    public void FreezeServerIdentity()
    {
        lock (_gate)
        {
            _serverOverride = _server;
        }
    }

    public void ClearServerIdentityOverride()
    {
        lock (_gate)
        {
            _serverOverride = null;
        }
    }

    public void SetServerIdentityOverride(string sourceHash, string artifactDigest)
    {
        lock (_gate)
        {
            _serverOverride = new ServerRuntimeIdentity(sourceHash, artifactDigest);
        }
    }

    public void FreezeRunnerIdentity()
    {
        lock (_gate)
        {
            var old = _runner;
            if (old is null)
                throw new InvalidOperationException("No runner runtime identity is available to freeze.");
            RunnerIdentityTransform = current => current with
            {
                BuildGitHash = old.BuildGitHash,
                ArtifactDigest = old.ArtifactDigest,
            };
        }
    }

    public string? SystemInfoJson()
    {
        ServerRuntimeIdentity? server;
        RunnerRuntimeIdentity? runner;
        string? runnerServiceStatusOverride;
        lock (_gate)
        {
            server = _server;
            runner = _runner;
            runnerServiceStatusOverride = _runnerServiceStatusOverride;
        }

        if (server is null)
            return null;

        return JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                running = new { gitHash = server.SourceHash, artifactDigest = server.ArtifactDigest },
                services = new { runner = runnerServiceStatusOverride ?? (runner is null ? "inactive" : "active") },
            },
        });
    }

    public void OnCommandCompleted(string fileName, string[] args, string? workingDirectory, int exitCode)
    {
        if (exitCode != 0)
            return;

        if (string.Equals(fileName, "dotnet", StringComparison.Ordinal)
            && args.Length > 0
            && string.Equals(args[0], "publish", StringComparison.Ordinal))
        {
            var staging = ArgumentValue(args, "-o");
            if (!string.IsNullOrWhiteSpace(staging))
            {
                _files.AddDirectory(staging);
                _files.AddFile(Path.Combine(staging, "Mohist.Server.dll"), "fake-server-entry");
            }
            return;
        }

        if (string.Equals(fileName, "npm", StringComparison.Ordinal)
            && args.SequenceEqual(["run", "build", "-w", "packages/runner"])
            && !string.IsNullOrWhiteSpace(workingDirectory))
        {
            var sourceRoot = workingDirectory!;
            _files.AddDirectory(Path.Combine(sourceRoot, "packages", "runner", "dist"));
            var entryPoint = Path.Combine(sourceRoot, "packages", "runner", "dist", "cli.js");
            if (RunnerEntryPayload is { } payload)
                _files.AddFileBytes(entryPoint, payload);
            else
                _files.AddFile(entryPoint, "fake-runner-entry");
            _files.AddFile(Path.Combine(sourceRoot, "packages", "runner", "package.json"), "{\"name\":\"mohist-runner\"}");
            _files.AddDirectory(Path.Combine(sourceRoot, "node_modules", "mohist-runtime"));
            _files.AddFile(Path.Combine(sourceRoot, "node_modules", "mohist-runtime", "package.json"), "{\"name\":\"mohist-runtime\"}");
            return;
        }

        if (string.Equals(fileName, "cp", StringComparison.Ordinal)
            && args.Length == 3
            && string.Equals(args[0], "-RL", StringComparison.Ordinal))
        {
            _files.CopyRecursive(args[1], args[2]);
            return;
        }

        if (!string.Equals(fileName, "systemctl", StringComparison.Ordinal)
            || args.Length < 3
            || !string.Equals(args[0], "--user", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(args[1], "restart", StringComparison.Ordinal))
        {
            if (string.Equals(args[2], "mohist.service", StringComparison.Ordinal))
                SetServerFromServiceUnit();
            if (string.Equals(args[2], "mohist-runner.service", StringComparison.Ordinal))
                SetRunnerFromServiceUnit();
            return;
        }

        if (string.Equals(args[1], "stop", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                if (string.Equals(args[2], "mohist.service", StringComparison.Ordinal))
                    _server = null;
                if (string.Equals(args[2], "mohist-runner.service", StringComparison.Ordinal))
                    _runner = null;
            }
        }
    }

    public Task<RunnerRuntimeIdentity?> WaitForIdentityAsync(
        RunnerIdentityExpectation expected,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (MatchesInstance(_runner, expected))
                return Task.FromResult<RunnerRuntimeIdentity?>(_runner);

            var waiter = new TaskCompletionSource<RunnerRuntimeIdentity?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runnerWaiters.Add(new RunnerWaiter(expected, waiter));
            _runnerReadinessWaited.TrySetResult();
            return WaitForRunnerAsync(waiter, cancellationToken);
        }
    }

    private async Task<RunnerRuntimeIdentity?> WaitForRunnerAsync(
        TaskCompletionSource<RunnerRuntimeIdentity?> waiter,
        CancellationToken cancellationToken)
    {
        try
        {
            return await waiter.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _runnerWaiters.RemoveAll(candidate => ReferenceEquals(candidate.Completion, waiter));
            }
        }
    }

    private void SetServerFromServiceUnit()
    {
        var unit = ReadUnit("mohist.service");
        var runtimeRoot = UnitValue(unit, "WorkingDirectory=");
        var current = runtimeRoot is null ? null : ReadCurrentArtifact(runtimeRoot, "Mohist.Server.dll");
        var installed = current is not null && unit?.Contains(runtimeRoot + "/current/Mohist.Server.dll", StringComparison.Ordinal) == true
            ? new ServerRuntimeIdentity(current.SourceHash, current.ArtifactDigest)
            : null;
        lock (_gate)
        {
            _server = _serverOverride ?? installed;
        }
    }

    private void SetRunnerFromServiceUnit()
    {
        var unit = ReadUnit("mohist-runner.service");
        var runtimeRoot = UnitValue(unit, "WorkingDirectory=");
        var runnerId = UnitEnvironment(unit, "RUNNER_ID");
        var generation = UnitEnvironment(unit, "MOHIST_RUNTIME_GENERATION");
        var unitDigest = UnitEnvironment(unit, "MOHIST_ARTIFACT_DIGEST");
        var current = runtimeRoot is null ? null : ReadCurrentArtifact(runtimeRoot, Path.Combine("dist", "cli.js"));
        var installed = current is not null
            && runnerId is not null
            && generation is not null
            && string.Equals(unitDigest, current.ArtifactDigest, StringComparison.Ordinal)
            && unit?.Contains(runtimeRoot + "/current/dist/cli.js", StringComparison.Ordinal) == true
            && RunnerBuildInfoMatches(current.VersionRoot, current.SourceHash, current.ArtifactDigest)
            ? new RunnerRuntimeIdentity(runnerId, generation, current.SourceHash, current.ArtifactDigest, "online", "connected")
            : null;

        List<RunnerWaiter> waiters;
        RunnerRuntimeIdentity? runtime;
        lock (_gate)
        {
            var candidate = installed is null ? null : RunnerIdentityTransform?.Invoke(installed) ?? installed;
            if (HoldRunnerReadiness)
            {
                _pendingRunner = candidate;
                _runner = null;
            }
            else
            {
                _pendingRunner = null;
                _runner = candidate;
            }
            runtime = _runner;
            waiters = _runnerWaiters.ToList();
        }

        if (runtime is null)
            return;
        foreach (var waiter in waiters.Where(waiter => MatchesInstance(runtime, waiter.Expected)))
            waiter.Completion.TrySetResult(runtime);
    }

    private FakeArtifact? ReadCurrentArtifact(string runtimeRoot, string entryPoint)
    {
        var versionRoot = _files.ReadDirectorySymbolicLink(Path.Combine(runtimeRoot, "current"));
        if (string.IsNullOrWhiteSpace(versionRoot)
            || !_files.HasFile(Path.Combine(versionRoot, entryPoint))
            || !_files.HasFile(Path.Combine(versionRoot, "mohist-build.json")))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(_files.Read(Path.Combine(versionRoot, "mohist-build.json")));
            var root = document.RootElement;
            if (!root.TryGetProperty("gitHash", out var hash)
                || hash.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("artifactDigest", out var digest)
                || digest.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("entryPoint", out var manifestEntryPoint)
                || manifestEntryPoint.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("payloadFiles", out var manifestPayloadFiles)
                || manifestPayloadFiles.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sourceHash = hash.GetString();
            var artifactDigest = digest.GetString();
            var expectedEntryPoint = entryPoint.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(sourceHash)
                || string.IsNullOrWhiteSpace(artifactDigest)
                || !IsDigest(artifactDigest)
                || !string.Equals(manifestEntryPoint.GetString(), expectedEntryPoint, StringComparison.Ordinal))
            {
                return null;
            }

            var manifestPayload = new List<string>();
            foreach (var file in manifestPayloadFiles.EnumerateArray())
            {
                var path = file.ValueKind == JsonValueKind.String ? file.GetString() : null;
                if (string.IsNullOrWhiteSpace(path))
                    return null;
                manifestPayload.Add(path);
            }
            if (!manifestPayload.SequenceEqual(manifestPayload.OrderBy(path => path, StringComparer.Ordinal), StringComparer.Ordinal)
                || manifestPayload.Distinct(StringComparer.Ordinal).Count() != manifestPayload.Count)
            {
                return null;
            }

            var runnerArtifact = string.Equals(expectedEntryPoint, "dist/cli.js", StringComparison.Ordinal);
            var payload = _files.EnumerateFiles(versionRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(versionRoot, path).Replace('\\', '/'))
                .Where(path => !string.Equals(path, "mohist-build.json", StringComparison.Ordinal)
                    && !(runnerArtifact && string.Equals(path, "dist/build-info.json", StringComparison.Ordinal)))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (!payload.SequenceEqual(manifestPayload, StringComparer.Ordinal)
                || !string.Equals(ComputePayloadDigest(versionRoot, payload), artifactDigest, StringComparison.Ordinal))
            {
                return null;
            }

            return new FakeArtifact(sourceHash, artifactDigest, versionRoot);
        }
        catch
        {
            return null;
        }
    }

    private string ComputePayloadDigest(string versionRoot, IReadOnlyList<string> payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in payload)
        {
            var pathBytes = Encoding.UTF8.GetBytes(relativePath);
            AppendUInt64(hash, (ulong)pathBytes.Length);
            hash.AppendData(pathBytes);
            using var stream = _files.OpenRead(Path.Combine(versionRoot, relativePath));
            AppendPayloadDigest(hash, stream);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendPayloadDigest(IncrementalHash manifestHash, Stream payload)
    {
        using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        ulong length = 0;
        int read;
        while ((read = payload.Read(buffer, 0, buffer.Length)) > 0)
        {
            payloadHash.AppendData(buffer, 0, read);
            length += (uint)read;
        }

        AppendUInt64(manifestHash, length);
        manifestHash.AppendData(payloadHash.GetHashAndReset());
    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static bool IsDigest(string? value) =>
        value is { Length: 64 }
        && value.All(character => (character is >= 'a' and <= 'f') || (character is >= '0' and <= '9'));

    private bool RunnerBuildInfoMatches(string versionRoot, string sourceHash, string artifactDigest)
    {
        var buildInfo = Path.Combine(versionRoot, "dist", "build-info.json");
        if (!_files.HasFile(buildInfo))
            return false;
        try
        {
            using var document = JsonDocument.Parse(_files.Read(buildInfo));
            return document.RootElement.TryGetProperty("gitHash", out var hash)
                && document.RootElement.TryGetProperty("artifactDigest", out var digest)
                && string.Equals(hash.GetString(), sourceHash, StringComparison.Ordinal)
                && string.Equals(digest.GetString(), artifactDigest, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private string? ReadUnit(string unitName)
    {
        var path = Path.Combine(UnitDir, unitName);
        return _files.HasFile(path) ? _files.Read(path) : null;
    }

    private static string? UnitValue(string? unit, string prefix) =>
        unit?.Split('\n').FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static string? UnitEnvironment(string? unit, string name)
    {
        var prefix = $"Environment=\"{name}=";
        var value = unit?.Split('\n').FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        return value?.TrimEnd('"');
    }

    private static string? ArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        }
        return null;
    }

    private static bool MatchesInstance(RunnerRuntimeIdentity? runtime, RunnerIdentityExpectation expected) =>
        runtime is not null
        && string.Equals(runtime.RunnerId, expected.RunnerId, StringComparison.Ordinal)
        && string.Equals(runtime.RuntimeGeneration, expected.RuntimeGeneration, StringComparison.Ordinal);

    private sealed record FakeArtifact(string SourceHash, string ArtifactDigest, string VersionRoot);
    private sealed record ServerRuntimeIdentity(string SourceHash, string ArtifactDigest);
    private sealed record RunnerWaiter(
        RunnerIdentityExpectation Expected,
        TaskCompletionSource<RunnerRuntimeIdentity?> Completion);
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
    public Action<string, string[], string?, int>? OnCompleted { get; set; }

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
        {
            OnCompleted?.Invoke(fileName, args, workingDirectory, resultRule.ExitCode);
            return Task.FromResult((resultRule.ExitCode, resultRule.Stdout, resultRule.Stderr));
        }
        var rule = _exitCodeRules.FirstOrDefault(rule => rule.FileName == fileName && rule.Match(args));
        var stdoutRule = _stdoutRules.FirstOrDefault(rule => rule.FileName == fileName && rule.Match(args));
        if (fileName == "git" && args.SequenceEqual(["rev-parse", "HEAD"])
            && rule.Match is null && stdoutRule.Match is null)
        {
            OnCompleted?.Invoke(fileName, args, workingDirectory, 0);
            return Task.FromResult((0, "abcdef0\n", ""));
        }

        var code = rule.Match is not null ? rule.ExitCode : _exitCodes.Count > 0 ? _exitCodes.Dequeue() : 0;
        var stdout = stdoutRule.Match is not null ? stdoutRule.Stdout : _stdout.Count > 0 ? _stdout.Dequeue() : "";
        var stderr = _stderr.Count > 0 ? _stderr.Dequeue() : "";
        OnCompleted?.Invoke(fileName, args, workingDirectory, code);
        return Task.FromResult((code, stdout, stderr));
    }
}

internal interface IScopedRuntimeHttpHandler
{
    void BindRuntime(FakeRuntimeInstallState runtime);
}

internal sealed class SequenceHttpHandler : HttpMessageHandler, IScopedRuntimeHttpHandler
{
    private const string DefaultSystemInfoJson =
        "{\"success\":true,\"data\":{\"running\":{\"gitHash\":\"abcdef0\"},\"services\":{\"runner\":\"active\"}}}";

    private readonly ResponseSpec?[] _responses;
    private string? _systemInfoJson;
    private FakeRuntimeInstallState? _runtime;

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

    public void BindRuntime(FakeRuntimeInstallState runtime) => _runtime = runtime;

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
                Content = new StringContent(_runtime?.SystemInfoJson() ?? _systemInfoJson ?? DefaultSystemInfoJson)
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

internal sealed class OutcomeCapturingHttpHandler : HttpMessageHandler, IScopedRuntimeHttpHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _systemInfoJson;
    private FakeRuntimeInstallState? _runtime;

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

    public void BindRuntime(FakeRuntimeInstallState runtime) => _runtime = runtime;

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
                Content = new StringContent(_runtime?.SystemInfoJson() ?? _systemInfoJson)
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
    private readonly Dictionary<string, byte[]> _fileBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _directoryLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private string _currentDirectory = "/";

    public Func<string, string, Exception?>? DirectoryLinkReplaceFailure { get; set; }
    public Func<string, Exception?>? DirectoryLinkDeleteFailure { get; set; }
    public Action<string, string>? OnDirectoryLinkReplace { get; set; }

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

    public IReadOnlyDictionary<string, string> DirectoryLinks
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_directoryLinks, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void AddFile(string path, string content)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            SetFile(normalized, Encoding.UTF8.GetBytes(content), content);
        }
    }

    public void AddFileBytes(string path, byte[] contents)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            SetFile(normalized, contents, Encoding.UTF8.GetString(contents));
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
            return _files.ContainsKey(normalized) || _directories.Contains(normalized) || _directoryLinks.ContainsKey(normalized);
        }
    }

    public bool DirectoryExists(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            if (_directories.Contains(normalized))
                return true;
            if (_directoryLinks.ContainsKey(normalized))
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
            _fileBytes.Remove(normalized);
            _directoryLinks.Remove(normalized);
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
                _fileBytes.Remove(key);
            }
            foreach (var link in _directoryLinks.Keys.Where(k => k == normalized || StartsWithDirectory(k, normalized)).ToArray())
            {
                _directoryLinks.Remove(link);
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
                var bytes = _fileBytes[key];
                _files.Remove(key);
                _fileBytes.Remove(key);
                SetFile(newKey, bytes, content);
            }
        }
    }

    public void CopyRecursive(string source, string destination)
    {
        var sourceKey = Normalize(source);
        var destinationKey = Normalize(destination);
        lock (_gate)
        {
            if (_files.TryGetValue(sourceKey, out var file))
            {
                SetFile(destinationKey, _fileBytes[sourceKey], file);
                return;
            }

            var prefix = sourceKey.EndsWith(Path.DirectorySeparatorChar)
                ? sourceKey
                : sourceKey + Path.DirectorySeparatorChar;
            var sourceFiles = _files
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (sourceFiles.Length == 0)
                throw new DirectoryNotFoundException($"Fake filesystem has no directory at '{source}'.");

            _directories.Add(destinationKey);
            foreach (var (path, contents) in sourceFiles)
            {
                var relative = path[prefix.Length..];
                SetFile(Path.Combine(destinationKey, relative), _fileBytes[path], contents);
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
            var bytes = _fileBytes[sourceKey];
            _fileBytes.Remove(sourceKey);
            SetFile(destKey, bytes, content);
        }
    }

    public string ReadAllText(string path) => Read(path);

    public Task<string> ReadAllTextAsync(string path) => Task.FromResult(Read(path));

    public void WriteAllText(string path, string contents)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            SetFile(normalized, Encoding.UTF8.GetBytes(contents), contents);
        }
    }

    public void WriteAllBytes(string path, byte[] contents)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            SetFile(normalized, contents, Encoding.UTF8.GetString(contents));
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

    public Stream OpenRead(string path)
    {
        lock (_gate)
        {
            if (_fileBytes.TryGetValue(Normalize(path), out var contents))
                return new MemoryStream(contents.ToArray(), writable: false);
            throw new FileNotFoundException($"Fake filesystem has no file at '{path}'.");
        }
    }

    public Stream OpenWrite(string path) => new RecordingStream(this, path);

    public void ReplaceDirectorySymbolicLink(string linkPath, string targetPath)
    {
        var normalizedLink = Normalize(linkPath);
        var normalizedTarget = Normalize(targetPath);
        var failure = DirectoryLinkReplaceFailure?.Invoke(normalizedLink, normalizedTarget);
        if (failure is not null)
            throw failure;
        OnDirectoryLinkReplace?.Invoke(normalizedLink, normalizedTarget);
        lock (_gate)
        {
            _directoryLinks[normalizedLink] = normalizedTarget;
        }
    }

    public string? ReadDirectorySymbolicLink(string linkPath)
    {
        lock (_gate)
        {
            return _directoryLinks.TryGetValue(Normalize(linkPath), out var target) ? target : null;
        }
    }

    public void DeleteDirectorySymbolicLink(string linkPath)
    {
        var normalizedLink = Normalize(linkPath);
        var failure = DirectoryLinkDeleteFailure?.Invoke(normalizedLink);
        if (failure is not null)
            throw failure;
        lock (_gate)
        {
            _directoryLinks.Remove(normalizedLink);
        }
    }

    private static string Normalize(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private void SetFile(string path, byte[] contents, string text)
    {
        _files[path] = text;
        _fileBytes[path] = contents.ToArray();
    }

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
            _owner.WriteAllBytes(_path, ToArray());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _owner.WriteAllBytes(_path, ToArray());
            }
            base.Dispose(disposing);
        }
    }
}
