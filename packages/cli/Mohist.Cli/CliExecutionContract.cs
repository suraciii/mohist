using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal enum CliExitOutcome
{
    Success,
    OperationFailure,
    UsageFailure,
    Cancelled,
}

internal static class CliExitCode
{
    public static int For(CliExitOutcome outcome) => outcome switch
    {
        CliExitOutcome.Success => 0,
        CliExitOutcome.OperationFailure => 1,
        CliExitOutcome.UsageFailure => 2,
        CliExitOutcome.Cancelled => 130,
        _ => 1,
    };
}

internal enum CliTransportAttemptState
{
    NotSubmitted,
    OutcomeUnknown,
    Completed,
}

internal static class CliTransportAttempt
{
    public static CliTransportAttemptState ClassifyFailure(bool mutating, bool sendStarted) =>
        !mutating || !sendStarted
            ? CliTransportAttemptState.NotSubmitted
            : CliTransportAttemptState.OutcomeUnknown;

    public static bool ShouldRetry(CliTransportAttemptState state) =>
        state != CliTransportAttemptState.OutcomeUnknown;
}

internal sealed record CliFailure(
    string Code,
    string Message,
    JsonNode? Details,
    CliTransportAttemptState AttemptState = CliTransportAttemptState.Completed)
{
    public int ExitCode => CliExitCode.For(CliExitOutcome.OperationFailure);
}

internal sealed record CliResponseResult(
    JsonNode? Data,
    CliFailure? Failure,
    HttpStatusCode StatusCode)
{
    public bool IsSuccess => Failure is null;
}

internal interface ICliEnvironment
{
    string? Get(string name);
}

internal sealed class SystemCliEnvironment : ICliEnvironment
{
    public static readonly SystemCliEnvironment Instance = new();

    private SystemCliEnvironment() { }

    public string? Get(string name) => SystemEnvironmentVariableProvider.Instance.GetEnvironmentVariable(name);
}

internal interface ICliTerminal
{
    bool IsInputInteractive { get; }
    Task<string?> ReadHiddenAsync(TextReader input, CancellationToken cancellationToken = default);
}

internal sealed class CliTerminal : ICliTerminal
{
    public CliTerminal(bool isInputInteractive) => IsInputInteractive = isInputInteractive;

    public bool IsInputInteractive { get; }

    public async Task<string?> ReadHiddenAsync(TextReader input, CancellationToken cancellationToken = default)
    {
        if (input != Console.In || Console.IsInputRedirected || !IsInputInteractive)
            return await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        var value = new System.Text.StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }
            if (key.Key is ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }

    public static CliTerminal From(TextReader input) =>
        new(input == Console.In ? !Console.IsInputRedirected : input != TextReader.Null);
}

internal sealed class CliInvocation
{
    public CliInvocation(
        TextWriter output,
        TextWriter error,
        TextReader input,
        ICliTerminal terminal,
        ICliEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        Output = output;
        Error = error;
        Input = input;
        Terminal = terminal;
        Environment = environment;
        CancellationToken = cancellationToken;
    }

    public TextWriter Output { get; }
    public TextWriter Error { get; }
    public TextReader Input { get; }
    public ICliTerminal Terminal { get; }
    public ICliEnvironment Environment { get; }
    public CancellationToken CancellationToken { get; }

    public bool PromptsEnabled =>
        Terminal.IsInputInteractive &&
        !string.Equals(Environment.Get("MOHIST_PROMPT_DISABLED"), "1", StringComparison.Ordinal);

    public async Task<bool> RequirePromptAsync(string requirement, string explicitInput, Func<Task<bool>> prompt)
    {
        if (PromptsEnabled)
            return await prompt().ConfigureAwait(false);

        await Error.WriteLineAsync(
            $"{requirement} is required; provide it explicitly with {explicitInput} (prompts are disabled for non-interactive input).")
            .ConfigureAwait(false);
        return false;
    }
}

internal sealed class CliHintResolver
{
    private readonly IReadOnlyDictionary<string, string> _hints;

    public CliHintResolver(IReadOnlyDictionary<string, string>? hints = null)
    {
        _hints = hints ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public string? Resolve(CliFailure failure) =>
        _hints.TryGetValue(failure.Code, out var hint) ? hint : null;
}

internal sealed class CliResultWriter
{
    private readonly CliInvocation _invocation;
    private readonly CliHintResolver _hints;

    public CliResultWriter(CliInvocation invocation, CliHintResolver? hints = null)
    {
        _invocation = invocation;
        _hints = hints ?? new CliHintResolver();
    }

    public async Task<int> WriteSuccessAsync(JsonNode? result)
    {
        await _invocation.Output.WriteLineAsync(result?.ToJsonString(MohistCliApi.JsonOutputOptions) ?? "OK")
            .ConfigureAwait(false);
        return CliExitCode.For(CliExitOutcome.Success);
    }

    public async Task<int> WriteFailureAsync(CliFailure failure)
    {
        var context = failure.Details is null ? string.Empty : $" details={failure.Details.ToJsonString()}";
        var state = failure.AttemptState == CliTransportAttemptState.NotSubmitted
            ? " request-not-submitted"
            : failure.AttemptState == CliTransportAttemptState.OutcomeUnknown
                ? " operation-result-unknown"
                : string.Empty;
        await _invocation.Error.WriteLineAsync(
            $"{failure.Message} (code={failure.Code}){state}{context}").ConfigureAwait(false);

        var hint = _hints.Resolve(failure);
        if (hint is not null)
            await _invocation.Error.WriteLineAsync($"hint: {hint}").ConfigureAwait(false);
        return failure.ExitCode;
    }

    public Task<int> WriteUsageFailureAsync(string message, string usage) =>
        WriteUsageFailureCoreAsync(message, usage);

    private async Task<int> WriteUsageFailureCoreAsync(string message, string usage)
    {
        await _invocation.Error.WriteLineAsync(message).ConfigureAwait(false);
        await _invocation.Error.WriteLineAsync(usage).ConfigureAwait(false);
        return CliExitCode.For(CliExitOutcome.UsageFailure);
    }
}

internal sealed class CliResponseReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;

    public CliResponseReader(HttpClient http) => _http = http;

    public async Task<CliResponseResult> ReadAsync(
        HttpMethod method,
        string path,
        object? body = null,
        bool mutating = false,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        try
        {
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? node = string.IsNullOrWhiteSpace(content) ? null : JsonNode.Parse(content);
            var success = node?["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode;
            if (success)
                return new CliResponseResult(node?["data"], null, response.StatusCode);

            var message = node?["error"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Request failed";
            var rawCode = node?["code"]?.GetValue<string>();
            var code = string.IsNullOrWhiteSpace(rawCode)
                ? $"http-{(int)response.StatusCode}"
                : rawCode!;
            return new CliResponseResult(
                null,
                new CliFailure(code, message, node?["details"], CliTransportAttemptState.Completed),
                response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return new CliResponseResult(
                null,
                new CliFailure($"http-{(int)HttpStatusCode.BadGateway}", ex.Message, null),
                HttpStatusCode.BadGateway);
        }
        catch (HttpRequestException ex)
        {
            var state = CliTransportAttempt.ClassifyFailure(mutating, sendStarted: true);
            return new CliResponseResult(
                null,
                new CliFailure("server-unavailable", ex.Message, null, state),
                (HttpStatusCode)0);
        }
    }
}
