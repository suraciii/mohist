using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mohist.Cli;

/// <summary>
/// Performs runtime consistency checks against the freshly built and restarted Mohist
/// components. Extracted from <see cref="SourceCodeUpdater"/> so the facade no longer mixes
/// stage orchestration with check implementation. Each <c>Check*Async</c> method is internal so
/// it can be unit-tested directly without going through the full update pipeline.
/// </summary>
internal sealed class RuntimeConsistencyValidator
{
    private static readonly Regex AssetPathRegex = new(
        """(?:src|href)=["'](?<path>/assets/[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly ICommandExecutor _commandExecutor;
    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly Func<string?>? _getUserHome;
    private readonly TextWriter _out;

    public RuntimeConsistencyValidator(
        HttpClient http,
        ICommandExecutor commandExecutor,
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment,
        TextWriter output,
        Func<string?>? getUserHome = null)
    {
        _http = http;
        _commandExecutor = commandExecutor;
        _fileSystem = fileSystem;
        _environment = environment;
        _getUserHome = getUserHome;
        _out = output;
    }

    internal async Task<RuntimeCheckResult> CheckCliBinaryAsync(UpdateContext context, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(context.CliPath))
        {
            return new RuntimeCheckResult("CLI binary", RuntimeCheckOutcome.Fail,
                "CLI binary path was not resolved; cannot invoke mo --version. Reinstall with 'mo update' or pass --cli-path.");
        }

        try
        {
            var (exitCode, stdout, stderr) = await _commandExecutor.ExecuteAsync(context.CliPath, ["--version"], null);
            if (exitCode != 0)
            {
                return new RuntimeCheckResult("CLI binary", RuntimeCheckOutcome.Fail,
                    $"mo --version exited with code {exitCode}: {stderr.Trim()}");
            }

            var versionOutput = stdout.Trim();
            if (string.IsNullOrWhiteSpace(versionOutput))
            {
                return new RuntimeCheckResult("CLI binary", RuntimeCheckOutcome.Warn,
                    "mo --version reported an empty version string.");
            }

            return new RuntimeCheckResult("CLI binary", RuntimeCheckOutcome.Pass,
                $"mo --version reported '{versionOutput}'");
        }
        catch (Exception ex)
        {
            return new RuntimeCheckResult("CLI binary", RuntimeCheckOutcome.Fail,
                $"mo --version failed: {ex.Message}");
        }
    }

    internal async Task<RuntimeCheckResult> CheckServerIdentityAsync(UpdateContext context, CancellationToken token)
    {
        var info = await TryGetSystemInfoAsync(token);
        if (info is null)
        {
            return new RuntimeCheckResult("Server identity", RuntimeCheckOutcome.Fail,
                "GET /api/system/info did not respond");
        }

        var runningHash = info.Running?.GitHash;
        if (string.IsNullOrWhiteSpace(runningHash))
        {
            return new RuntimeCheckResult("Server identity", RuntimeCheckOutcome.Warn,
                "Server reported an empty git hash; cannot verify identity");
        }

        var sourceHead = await TryGetSourceHeadAsync(context);
        if (string.IsNullOrWhiteSpace(sourceHead))
        {
            return new RuntimeCheckResult("Server identity", RuntimeCheckOutcome.Warn,
                "Source HEAD could not be determined; skipping identity check");
        }

        if (!string.Equals(runningHash, sourceHead, StringComparison.Ordinal))
        {
            return new RuntimeCheckResult("Server identity", RuntimeCheckOutcome.Warn,
                $"Running server git hash '{runningHash}' does not match source HEAD '{sourceHead}'");
        }

        return new RuntimeCheckResult("Server identity", RuntimeCheckOutcome.Pass,
            $"Server identity matches source HEAD '{sourceHead}'");
    }

    internal async Task<RuntimeCheckResult> CheckWebAssetsAsync(UpdateContext context, CancellationToken token)
    {
        try
        {
            using var index = await _http.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, token);
            if (!index.IsSuccessStatusCode)
            {
                return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Fail,
                    $"GET / returned {(int)index.StatusCode} {index.StatusCode}");
            }

            var contentType = index.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Fail,
                    $"GET / returned content type '{contentType ?? "unknown"}', expected text/html");
            }

            var html = await index.Content.ReadAsStringAsync(token);
            var assetPath = FindFirstAssetPath(html);
            if (assetPath is null)
            {
                return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Fail,
                    "GET / did not reference a /assets/* bundle");
            }

            using var asset = await _http.GetAsync(assetPath, HttpCompletionOption.ResponseHeadersRead, token);
            if (asset.StatusCode != HttpStatusCode.OK)
            {
                return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Fail,
                    $"GET {assetPath} returned {(int)asset.StatusCode} {asset.StatusCode}");
            }

            return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Pass,
                $"Web root and {assetPath} respond with expected content");
        }
        catch (Exception ex)
        {
            return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Fail,
                $"Web asset check failed: {ex.Message}");
        }
    }

    internal async Task<RuntimeCheckResult> CheckRunnerConnectionAsync(UpdateContext context, CancellationToken token)
    {
        var info = await TryGetSystemInfoAsync(token);
        if (info is null)
        {
            return new RuntimeCheckResult("Runner connection", RuntimeCheckOutcome.Fail,
                "GET /api/system/info did not respond");
        }

        var runner = info.Services?.Runner;
        if (string.IsNullOrWhiteSpace(runner))
        {
            return new RuntimeCheckResult("Runner connection", RuntimeCheckOutcome.Fail,
                "Server did not report a runner service state");
        }

        if (string.Equals(runner, "active", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeCheckResult("Runner connection", RuntimeCheckOutcome.Pass,
                "Runner service is active");
        }

        return new RuntimeCheckResult("Runner connection", RuntimeCheckOutcome.Fail,
            $"Runner service is '{runner}'; expected 'active'");
    }

    internal async Task<RuntimeCheckResult> CheckManagedSkillAssetsAsync(UpdateContext context, CancellationToken token)
    {
        await Task.CompletedTask;
        var assetRoot = ResolveManagedSkillAssetRoot();
        if (!_fileSystem.DirectoryExists(assetRoot))
        {
            return new RuntimeCheckResult("Managed skill assets", RuntimeCheckOutcome.Warn,
                $"Managed skill assets are missing at '{assetRoot}'. Run 'mo skill install' to restore.");
        }

        try
        {
            var hasSkill = _fileSystem
                .EnumerateFiles(assetRoot, "SKILL.md", SearchOption.AllDirectories)
                .Any();

            if (!hasSkill)
            {
                return new RuntimeCheckResult("Managed skill assets", RuntimeCheckOutcome.Warn,
                    $"Managed skill assets at '{assetRoot}' contain no skill. Run 'mo skill install' to restore.");
            }
        }
        catch (Exception ex)
        {
            return new RuntimeCheckResult("Managed skill assets", RuntimeCheckOutcome.Warn,
                $"Failed to inspect managed skill assets at '{assetRoot}': {ex.Message}");
        }

        return new RuntimeCheckResult("Managed skill assets", RuntimeCheckOutcome.Pass,
            $"Skill assets present at '{assetRoot}'");
    }

    private string ResolveManagedSkillAssetRoot()
    {
        var home = _getUserHome?.Invoke();
        if (string.IsNullOrWhiteSpace(home))
            home = _environment.GetEnvironmentVariable(SkillAssetRootResolver.HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".mohist", "cli", "skill-data");
        return Path.Combine(AppContext.BaseDirectory, "skill-data");
    }

    internal async Task<RuntimeIdentityVerification> VerifyServerRuntimeIdentityAsync(
        InstalledRuntimeArtifact expected,
        CancellationToken token)
    {
        var info = await TryGetSystemInfoAsync(token);
        var actual = info?.Running?.GitHash;
        var actualDigest = info?.Running?.ArtifactDigest;
        if (string.IsNullOrWhiteSpace(actual))
        {
            return new RuntimeIdentityVerification(
                expected.SourceHash,
                null,
                false,
                "Server did not report a runtime gitHash",
                expected.ArtifactDigest,
                actualDigest);
        }

        if (string.IsNullOrWhiteSpace(actualDigest))
        {
            return new RuntimeIdentityVerification(
                expected.SourceHash,
                actual,
                false,
                "Server did not report an installed artifactDigest",
                expected.ArtifactDigest,
                null);
        }

        var matches = string.Equals(expected.SourceHash, actual, StringComparison.Ordinal)
            && string.Equals(expected.ArtifactDigest, actualDigest, StringComparison.Ordinal);
        return new RuntimeIdentityVerification(
            expected.SourceHash,
            actual,
            matches,
            matches
                ? "Server runtime identity matches expected source and artifact identities"
                : $"server source identity expected {expected.SourceHash}, actual {actual}; artifact identity expected {expected.ArtifactDigest}, actual {actualDigest}",
            expected.ArtifactDigest,
            actualDigest);
    }

    private async Task<SystemInfoSnapshot?> TryGetSystemInfoAsync(CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync("/api/system/info", HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = document.RootElement;
            if (root.TryGetProperty("data", out var data))
                return data.Deserialize<SystemInfoSnapshot>(SystemInfoSnapshot.JsonOptions);
            return root.Deserialize<SystemInfoSnapshot>(SystemInfoSnapshot.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryGetSourceHeadAsync(UpdateContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.SourceHead))
            return context.SourceHead;

        try
        {
            var root = ResolveRepoRoot(context.RepoRoot);
            var (exitCode, stdout, _) = await _commandExecutor.ExecuteAsync("git", ["rev-parse", "HEAD"], root);
            if (exitCode != 0)
                return null;
            var head = stdout.Trim();
            if (string.IsNullOrWhiteSpace(head))
                return null;
            context.SourceHead = head;
            return head;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveRepoRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return explicitRoot.Replace('\\', '/');

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Mohist.sln")))
                return dir.FullName.Replace('\\', '/');
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory().Replace('\\', '/');
    }

    private static string? FindFirstAssetPath(string html)
    {
        var match = AssetPathRegex.Match(html);
        return match.Success ? match.Groups["path"].Value : null;
    }

    private sealed class SystemInfoRunningSnapshot
    {
        [System.Text.Json.Serialization.JsonPropertyName("gitHash")]
        public string? GitHash { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("artifactDigest")]
        public string? ArtifactDigest { get; set; }
    }

    private sealed class SystemInfoServiceSnapshot
    {
        [System.Text.Json.Serialization.JsonPropertyName("runner")]
        public string? Runner { get; set; }
    }

    private sealed class SystemInfoSnapshot
    {
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        [System.Text.Json.Serialization.JsonPropertyName("running")]
        public SystemInfoRunningSnapshot? Running { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("services")]
        public SystemInfoServiceSnapshot? Services { get; set; }
    }
}
