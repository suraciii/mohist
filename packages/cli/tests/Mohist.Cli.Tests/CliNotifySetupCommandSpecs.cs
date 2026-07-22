using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliNotifySetupCommandSpecs : IDisposable
{
    private readonly string _configPath;
    private readonly NotifyCommands.IHealthProbe? _previousProbe;
    private readonly Func<string> _previousConfigPath;
    private readonly Func<string> _previousSecretGenerator;

    public CliNotifySetupCommandSpecs()
    {
        _configPath = "/mohist-tests/notify/config.jsonc";
        _previousProbe = NotifyCommands.HealthProbeOverride;
        _previousConfigPath = NotifyCommands.ConfigPathOverride;
        _previousSecretGenerator = NotifyCommands.SecretGeneratorOverride;
        NotifyCommands.ConfigPathOverride = () => _configPath;
    }

    public void Dispose()
    {
        NotifyCommands.HealthProbeOverride = _previousProbe ?? new StubProbe(success: false);
        NotifyCommands.ConfigPathOverride = _previousConfigPath;
        NotifyCommands.SecretGeneratorOverride = _previousSecretGenerator;
    }

    [Fact]
    public void NotificationRoot_Help_ListsSetupSubcommand()
    {
        var exitCode = Run(["notification", "--help"], out var output, out _);

        Assert.Equal(0, exitCode);
        Assert.Contains("setup", output.ToString());
    }

    [Fact]
    public void NotifySetup_Help_DocumentsAllOptions()
    {
        var exitCode = Run(["notification", "setup", "--help"], out var output, out _);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("--health-base", text);
        Assert.Contains("--webhook-url", text);
        Assert.Contains("--platform", text);
    }

    [Fact]
    public async Task NotifySetup_ProbeDown_AbortsWithoutWriting_NoStackTrace()
    {
        var fileSystem = new FakeFileSystem();
        var httpHandler = new ThrowingHttpHandler();
        var executor = new FakeCommandExecutor();
        NotifyCommands.HealthProbeOverride = new StubProbe(success: false, reason: "connection refused");

        var (exitCode, stdout, stderr) = await RunAsync(
            httpHandler, fileSystem, executor, ["notification", "setup"]);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Hermes webhook platform is not started", stderr);
        Assert.Contains("docs/hermes-notifications.md", stderr);
        Assert.DoesNotContain("Connection refused", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ECONNREFUSED", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at System.", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(fileSystem.Files.Count == 0, "no file should be written when probe fails");
        Assert.Empty(executor.Invocations);
        Assert.Equal(0, httpHandler.CallCount);
    }

    [Fact]
    public async Task NotifySetup_ProbeNonSuccessStatus_AbortsWithoutStack()
    {
        NotifyCommands.HealthProbeOverride = new StubProbe(success: false, reason: "non-success status 503");

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            new FakeFileSystem(),
            new FakeCommandExecutor(),
            ["notification", "setup"]);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Hermes webhook platform is not started", stderr);
        Assert.DoesNotContain("503", stderr);
    }

    [Fact]
    public async Task NotifySetup_InvalidHealthBase_AbortsWithoutStackTrace()
    {
        var fileSystem = new FakeFileSystem();
        NotifyCommands.HealthProbeOverride = new StubProbe(success: false, reason: "invalid url");

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup", "--health-base", "127.0.0.1:8644"]);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Hermes webhook platform is not started", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at System.", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fileSystem.Files);
    }

    [Fact]
    public async Task ProbeHermesHealthAsync_NonAbsoluteHealthBase_ReturnsUnhealthy()
    {
        var result = await NotifyCommands.ProbeHermesHealthAsync("127.0.0.1:8644");

        Assert.False(result.IsHealthy);
        Assert.Equal("invalid url", result.FailureReason);
    }

    [Fact]
    public async Task NotifySetup_FreshConfig_WritesDerivedUrlAndSecretAndEnabledTypes()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(_configPath, """
            // unrelated pre-existing comment
            {
                "Mohist": {
                    "ServerPort": 3456
                }
            }
            """);
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        NotifyCommands.SecretGeneratorOverride = () => "test-secret-abc123";

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        var hermes = ReadHermes(fileSystem.ReadAllText(_configPath));
        Assert.Equal("http://127.0.0.1:8644/webhooks/mohist", hermes["WebhookUrl"]!.GetValue<string>());
        Assert.Equal("test-secret-abc123", hermes["Secret"]!.GetValue<string>());
        Assert.Equal(
            new[] { "approval_requested", "workflow_failed", "issue_completed" },
            hermes["EnabledTypes"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray());
        Assert.Contains("Wrote Mohist:Notifications:Hermes", stdout);
        Assert.Contains("mo update server", stdout);
    }

    [Fact]
    public async Task NotifySetup_FreshConfig_PrintsHermesSubscribeCommandWithSameSecret()
    {
        var fileSystem = new FakeFileSystem();
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        NotifyCommands.SecretGeneratorOverride = () => "shared-secret-xyz";

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup", "--platform", "telegram"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("hermes webhook subscribe mohist", stdout);
        Assert.Contains("--deliver telegram", stdout);
        Assert.Contains("--deliver-only", stdout);
        Assert.Contains("--secret shared-secret-xyz", stdout);
        Assert.Contains("--prompt '{body}'", stdout);
        Assert.DoesNotContain("--prompt-file", stdout);

        var hermes = ReadHermes(fileSystem.ReadAllText(_configPath));
        Assert.Equal("shared-secret-xyz", hermes["Secret"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("telegram test")]
    [InlineData("telegram; bad")]
    public async Task NotifySetup_InvalidPlatform_RejectsWithoutWriting(string platform)
    {
        var fileSystem = new FakeFileSystem();
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        NotifyCommands.SecretGeneratorOverride = () => "should-not-be-written";

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup", "--platform", platform]);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("--platform must contain only", stderr);
        Assert.Empty(fileSystem.Files);
    }

    [Fact]
    public async Task NotifySetup_MalformedConfig_AbortsWithoutWritingOrStackTrace()
    {
        const string malformed = "{ \"Mohist\": { \"Notifications\": ";
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(_configPath, malformed);
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        NotifyCommands.SecretGeneratorOverride = () => "should-not-be-written";

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup"]);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Could not parse Mohist config file", stderr);
        Assert.Contains(_configPath, stderr);
        Assert.DoesNotContain("JsonException", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at System.", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(malformed, fileSystem.ReadAllText(_configPath));
    }

    [Fact]
    public async Task NotifySetup_NoPlatform_PrintsPlaceholderGuidance()
    {
        var fileSystem = new FakeFileSystem();
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        NotifyCommands.SecretGeneratorOverride = () => "secret-no-platform";

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        var cleaned = StripArgs(stdout, "--deliver");
        Assert.DoesNotContain("--deliver ", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("--deliver\t", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("--deliver\"", cleaned, StringComparison.Ordinal);
        Assert.Contains("Replace <platform>", stdout);
    }

    [Fact]
    public async Task NotifySetup_WithDeliverChatId_FoldsItIntoSubscribeCommand()
    {
        // Platforms without a default home channel (weixin and similar) reject
        // delivery with "No chat_id" unless --deliver-chat-id is supplied.
        // setup must fold the provided chat id into the printed command.
        var fileSystem = new FakeFileSystem();
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        NotifyCommands.SecretGeneratorOverride = () => "secret-chatid";

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup", "--platform", "weixin", "--deliver-chat-id", "wx-user-123"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("--deliver weixin --deliver-chat-id wx-user-123", stdout);
        // When a chat id is supplied, the "no home channel" hint is not needed.
        Assert.DoesNotContain("no default home channel", stdout);
    }

    [Fact]
    public async Task NotifySetup_PlatformWithoutChatId_HintsAtHomeChannelRequirement()
    {
        // When a platform is chosen but no chat id is given, the user may hit a
        // silent "No chat_id" failure on platforms like weixin. setup should
        // point them at how to find the chat id rather than leaving them to
        // discover the failure themselves.
        var fileSystem = new FakeFileSystem();
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        NotifyCommands.SecretGeneratorOverride = () => "secret-no-chatid";

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup", "--platform", "weixin"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("hermes send --list <platform>", stdout);
        // The printed subscribe command itself carries no --deliver-chat-id
        // (the hint text mentions the flag, but the command doesn't use it).
        Assert.DoesNotContain("--deliver weixin --deliver-chat-id", stdout);
    }

    [Fact]
    public async Task NotifySetup_OverwritePromptAccepted_WritesNewSecret()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(_configPath, ExistingHermesJson);
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        NotifyCommands.SecretGeneratorOverride = () => "freshly-minted";
        var stdin = new StringReader("y\n");

        var (exitCode, stdout, _) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup"],
            stdin);

        Assert.Equal(0, exitCode);
        var hermes = ReadHermes(fileSystem.ReadAllText(_configPath));
        Assert.Equal("http://127.0.0.1:8644/webhooks/mohist", hermes["WebhookUrl"]!.GetValue<string>());
        Assert.Equal("freshly-minted", hermes["Secret"]!.GetValue<string>());
    }

    [Fact]
    public async Task NotifySetup_OverwritePromptDeclined_WritesNothing()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(_configPath, ExistingHermesJson);
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        var stdin = new StringReader("n\n");
        var executor = new FakeCommandExecutor();

        var (exitCode, stdout, _) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            executor,
            ["notification", "setup"],
            stdin);

        Assert.Equal(0, exitCode);
        Assert.Contains("Aborted", stdout);
        Assert.Equal(ExistingHermesJson, fileSystem.ReadAllText(_configPath));
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public async Task NotifySetup_OverwritePromptBlankInput_TreatedAsNo()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(_configPath, ExistingHermesJson);
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        var stdin = new StringReader("\n");

        var (exitCode, stdout, _) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup"],
            stdin);

        Assert.Equal(0, exitCode);
        Assert.Equal(ExistingHermesJson, fileSystem.ReadAllText(_configPath));
        Assert.Contains("Aborted", stdout);
    }

    [Fact]
    public async Task NotifySetup_ExistingHermesScalar_PromptsBeforeOverwrite()
    {
        const string existingScalar = """
            {
              "Mohist": {
                "Notifications": {
                  "Hermes": "old-value"
                }
              }
            }
            """;
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(_configPath, existingScalar);
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        var stdin = new StringReader("n\n");

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup"],
            stdin);

        Assert.Equal(0, exitCode);
        Assert.Contains("Overwrite them?", stderr);
        Assert.Equal(existingScalar, fileSystem.ReadAllText(_configPath));
    }

    [Fact]
    public async Task NotifySetup_NonObjectNotificationsParent_AbortsWithoutWriting()
    {
        const string invalidShape = """
            {
              "Mohist": {
                "Notifications": "old-value"
              }
            }
            """;
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(_configPath, invalidShape);
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        NotifyCommands.SecretGeneratorOverride = () => "should-not-be-written";

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup"]);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Mohist:Notifications must be a JSON object", stderr);
        Assert.Equal(invalidShape, fileSystem.ReadAllText(_configPath));
    }

    [Fact]
    public async Task NotifySetup_NonInteractiveStdin_AbortsWithoutWritingNoHang()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(_configPath, ExistingHermesJson);
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        var stdin = new StringReader("");
        var executor = new FakeCommandExecutor();

        var (exitCode, stdout, stderr) = await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            executor,
            ["notification", "setup"],
            stdin);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(executor.Invocations);
        Assert.Equal(ExistingHermesJson, fileSystem.ReadAllText(_configPath));
    }

    [Fact]
    public async Task NotifySetup_Success_DoesNotInvokeHermesProcess()
    {
        var fileSystem = new FakeFileSystem();
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        NotifyCommands.SecretGeneratorOverride = () => "secret";
        var executor = new FakeCommandExecutor();

        await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            executor,
            ["notification", "setup", "--platform", "telegram"]);

        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public async Task NotifySetup_Success_WritesOnlyConfigFile()
    {
        var fileSystem = new FakeFileSystem();
        NotifyCommands.HealthProbeOverride = new StubProbe(success: true);
        NotifyCommands.SecretGeneratorOverride = () => "secret";

        await RunAsync(
            new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))),
            fileSystem,
            new FakeCommandExecutor(),
            ["notification", "setup"]);

        Assert.Single(fileSystem.Files);
        var onlyPath = fileSystem.Files.Keys.Single();
        Assert.Equal(_configPath, onlyPath);
    }

    [Fact]
    public void StripJsoncComments_LineComment_Removed()
    {
        var input = "{ // comment\n \"a\": 1 }";
        var output = NotifyCommands.StripJsoncComments(input);
        Assert.Equal("{ \n \"a\": 1 }", output);
    }

    [Fact]
    public void StripJsoncComments_BlockComment_Removed()
    {
        var input = "{ /* block */ \"a\": 1 }";
        var output = NotifyCommands.StripJsoncComments(input);
        Assert.Equal("{  \"a\": 1 }", output);
    }

    [Fact]
    public void StripJsoncComments_StringLiteral_PreservedVerbatim()
    {
        var input = "{ \"url\": \"http://127.0.0.1:8644/health\", \"note\": \"keep /* inside */\" }";
        var output = NotifyCommands.StripJsoncComments(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void StripJsoncComments_EscapedQuotes_Preserved()
    {
        var input = "{ \"escaped\": \"he said \\\"hi\\\"\" }";
        var output = NotifyCommands.StripJsoncComments(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void DeriveWebhookUrl_TrimsTrailingSlash()
    {
        Assert.Equal(
            "http://127.0.0.1:8644/webhooks/mohist",
            NotifyCommands.DeriveWebhookUrl("http://127.0.0.1:8644"));
        Assert.Equal(
            "http://127.0.0.1:8644/webhooks/mohist",
            NotifyCommands.DeriveWebhookUrl("http://127.0.0.1:8644/"));
        Assert.Equal(
            "http://127.0.0.1:8644/webhooks/mohist",
            NotifyCommands.DeriveWebhookUrl("http://127.0.0.1:8644///"));
    }

    [Fact]
    public void Base64UrlEncode_NoPaddingOrStandardAlphabets()
    {
        // Bytes chosen to trigger '+' or '/' in standard base64.
        var bytes = new byte[] { 0xFB, 0xFF, 0xBF };
        var encoded = NotifyCommands.Base64UrlEncode(bytes);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Fact]
    public void GenerateSecret_IsNonEmptyAndNonDeterministic()
    {
        var s1 = NotifyCommands.GenerateSecret();
        var s2 = NotifyCommands.GenerateSecret();
        Assert.False(string.IsNullOrWhiteSpace(s1));
        Assert.NotEqual(s1, s2);
    }

    [Fact]
    public void BuildHealthUrl_AppendsSlashHealth()
    {
        Assert.Equal(
            "http://127.0.0.1:8644/health",
            NotifyCommands.BuildHealthUrl("http://127.0.0.1:8644"));
        Assert.Equal(
            "http://127.0.0.1:8644/health",
            NotifyCommands.BuildHealthUrl("http://127.0.0.1:8644/"));
    }

    [Fact]
    public async Task LegacyRootNotify_NoLongerResolvesAndExitsNonZero()
    {
        // Per D1 (no aliases retained) the legacy `mo notify` root path is
        // removed outright — System.CommandLine surfaces a parse error and
        // the runner returns non-zero. No HTTP request must be issued.
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["notify"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    private const string ExistingHermesJson = """
        {
          "Mohist": {
            "Notifications": {
              "Hermes": {
                "WebhookUrl": "http://old.example/webhooks/mohist",
                "Secret": "old-secret",
                "EnabledTypes": [ "approval_requested" ]
              }
            }
          }
        }
        """;

    private static JsonObject ReadHermes(string text)
    {
        var root = JsonNode.Parse(text)!.AsObject();
        return root["Mohist"]!["Notifications"]!["Hermes"]!.AsObject();
    }

    private static string StripArgs(string text, string flag)
    {
        var idx = text.IndexOf(flag, StringComparison.Ordinal);
        if (idx < 0) return text;
        var end = idx + flag.Length;
        while (end < text.Length && text[end] != '\n')
            end++;
        return text[..idx] + text[end..];
    }

    private int Run(string[] args, out StringWriter output, out StringWriter error)
    {
        output = new StringWriter();
        error = new StringWriter();
        var http = new HttpClient(new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { }))))
        {
            BaseAddress = new Uri("http://localhost:3456"),
        };
        NotifyCommands.HealthProbeOverride = new StubProbe(success: false, reason: "default");
        return MohistCliCommands.RunAsync(http, args, output, error, new FakeFileSystem(), new FakeCommandExecutor())
            .GetAwaiter().GetResult();
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        HttpMessageHandler handler,
        FakeFileSystem fileSystem,
        FakeCommandExecutor executor,
        string[] args,
        TextReader? stdin = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await MohistCliCommands.RunAsync(
            http, args, output, error, fileSystem, executor, standardInput: stdin);
        return (code, output.ToString(), error.ToString());
    }

    private sealed class StubProbe : NotifyCommands.IHealthProbe
    {
        private readonly bool _success;
        private readonly string? _reason;

        public StubProbe(bool success, string? reason = null)
        {
            _success = success;
            _reason = reason;
        }

        public Task<NotifyCommands.HealthProbeResult> ProbeAsync(string healthBase)
        {
            if (_success)
                return Task.FromResult(NotifyCommands.HealthProbeResult.Healthy(healthBase + "/health"));
            return Task.FromResult(NotifyCommands.HealthProbeResult.Unhealthy(_reason ?? "stub-failure"));
        }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new HttpRequestException(
                "Connection refused (simulated).",
                new SocketException((int)SocketError.ConnectionRefused));
        }
    }
}
