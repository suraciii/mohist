using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit.Runner.Common;
using Xunit.Sdk;

[assembly: RegisterRunnerReporter(typeof(Mohist.Cli.Tests.Support.ExecutionLedgerReporter))]

namespace Mohist.Cli.Tests.Support;

public sealed class ExecutionLedgerReporter : IRunnerReporter
{
    public bool CanBeEnvironmentallyEnabled => true;

    public string Description => "writes the xUnit execution-time ledger for the CI duration gate";

    public bool ForceNoLogo => true;

    public bool IsEnvironmentallyEnabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ExecutionLedgerEnvironment.Path));

    public string? RunnerSwitch => null;

    public ValueTask<IRunnerReporterMessageHandler> CreateMessageHandler(IRunnerLogger logger, IMessageSink? diagnosticMessageSink) =>
        new(new ExecutionLedgerReporterMessageHandler());
}

internal static class ExecutionLedgerEnvironment
{
    public const string Path = "MOHIST_EXECUTION_LEDGER_PATH";
    public const string RunId = "MOHIST_EXECUTION_LEDGER_RUN_ID";
    public const string ManifestHash = "MOHIST_EXECUTION_LEDGER_MANIFEST_HASH";
    public const string ManifestCount = "MOHIST_EXECUTION_LEDGER_MANIFEST_COUNT";
    public const string AssemblyPath = "MOHIST_EXECUTION_LEDGER_ASSEMBLY_PATH";
    public const string AssemblySha256 = "MOHIST_EXECUTION_LEDGER_ASSEMBLY_SHA256";
    public const string Parallelism = "MOHIST_EXECUTION_LEDGER_PARALLELISM";
}

public sealed class ExecutionLedgerReporterMessageHandler : IRunnerReporterMessageHandler
{
    private readonly ExecutionLedgerCapture capture;
    private readonly MessageMetadataCache metadata = new();
    private string? error;

    public ExecutionLedgerReporterMessageHandler()
    {
        capture = ExecutionLedgerCapture.FromEnvironment();
    }

    public bool OnMessage(IMessageSinkMessage message)
    {
        try
        {
            CacheMetadata(message);

            if (message is ITestStarting starting && message is ITestMetadata testMetadata && message is ITestMessage testMessage)
            {
                var caseMetadata = message is ITestCaseMessage caseMessage
                    ? metadata.TryGetTestCaseMetadata(caseMessage)
                    : null;
                var collectionMetadata = message is ITestCollectionMessage collectionMessage
                    ? metadata.TryGetCollectionMetadata(collectionMessage)
                    : null;
                capture.RecordStarting(
                    testMessage.TestUniqueID,
                    testMetadata.TestDisplayName,
                    caseMetadata?.TestClassName,
                    collectionMetadata?.TestCollectionDisplayName,
                    starting.StartTime);
            }

            if (message is ITestResultMessage result && message is ITestMessage resultMessage)
            {
                var outcome = message switch
                {
                    ITestPassed => "passed",
                    ITestFailed => "failed",
                    ITestSkipped => "skipped",
                    ITestNotRun => "not-run",
                    _ => null,
                };
                if (outcome is not null)
                    capture.RecordResult(resultMessage.TestUniqueID, outcome, result.ExecutionTime, result.FinishTime);
            }
        }
        catch (Exception exception)
        {
            error ??= exception.ToString();
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        if (error is not null)
            return ValueTask.CompletedTask;

        try
        {
            capture.WriteToFile();
        }
        catch
        {
            // A missing or invalid ledger is fail-closed in the duration guard.
        }

        return ValueTask.CompletedTask;
    }

    private void CacheMetadata(IMessageSinkMessage message)
    {
        if (message is ITestAssemblyStarting assemblyStarting) metadata.Set(assemblyStarting);
        if (message is ITestCaseStarting caseStarting) metadata.Set(caseStarting);
        if (message is ITestClassStarting classStarting) metadata.Set(classStarting);
        if (message is ITestCollectionStarting collectionStarting) metadata.Set(collectionStarting);
        if (message is ITestMethodStarting methodStarting) metadata.Set(methodStarting);
        if (message is ITestStarting testStarting) metadata.Set(testStarting);
    }
}

public sealed record ExecutionLedgerMetadata(
    string RunId,
    string ManifestHash,
    int ManifestCount,
    string AssemblyPath,
    string AssemblySha256,
    string XunitVersion,
    string MtpVersion,
    string Parallelism,
    string OutputPath);

public sealed record ExecutionLedgerCase(
    string Uid,
    string Name,
    string Outcome,
    decimal ExecutionTimeSeconds,
    DateTimeOffset StartTime,
    DateTimeOffset FinishTime,
    string? ClassName,
    string? CollectionName);

public sealed class ExecutionLedgerDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string RunId { get; init; } = string.Empty;
    public string ManifestHash { get; init; } = string.Empty;
    public int ManifestCount { get; init; }
    public string AssemblyPath { get; init; } = string.Empty;
    public string AssemblySha256 { get; init; } = string.Empty;
    public string XunitVersion { get; init; } = string.Empty;
    public string MtpVersion { get; init; } = string.Empty;
    public string Parallelism { get; init; } = string.Empty;
    public string DurationSource { get; init; } = "xunit.v3.ITestResultMessage.ExecutionTime";
    public string DurationUnit { get; init; } = "seconds";
    public IReadOnlyList<ExecutionLedgerCase> Cases { get; init; } = [];
}

public sealed class ExecutionLedgerCapture
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object gate = new();
    private readonly ExecutionLedgerMetadata metadata;
    private readonly Dictionary<string, (string Name, string? ClassName, string? CollectionName, DateTimeOffset StartTime)> starts = [];
    private readonly List<ExecutionLedgerCase> results = [];

    public ExecutionLedgerCapture(ExecutionLedgerMetadata metadata)
    {
        this.metadata = metadata;
    }

    public static ExecutionLedgerCapture FromEnvironment()
    {
        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"missing execution ledger environment variable {name}");

        var assembly = typeof(ExecutionLedgerReporter).Assembly;
        var assemblyPath = assembly.Location;
        var expectedAssemblyPath = Required(ExecutionLedgerEnvironment.AssemblyPath);
        if (!string.Equals(assemblyPath, expectedAssemblyPath, StringComparison.Ordinal))
            throw new InvalidOperationException("execution ledger assembly path does not match the current test assembly");

        // The reporter is gate-runtime instrumentation, not a test fixture. Its
        // only physical I/O is the same-run ledger handoff consumed by the guard.
#pragma warning disable RS0030
        var assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant();
#pragma warning restore RS0030
        if (!string.Equals(assemblySha256, Required(ExecutionLedgerEnvironment.AssemblySha256), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("execution ledger assembly hash does not match the current test assembly");

        if (!int.TryParse(Required(ExecutionLedgerEnvironment.ManifestCount), out var manifestCount) || manifestCount <= 0)
            throw new InvalidOperationException("execution ledger manifest count is invalid");
        var manifestHash = Required(ExecutionLedgerEnvironment.ManifestHash);
        if (manifestHash.Length != 64 || manifestHash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("execution ledger manifest hash is invalid");
        var parallelism = Required(ExecutionLedgerEnvironment.Parallelism);

        var mtpVersion = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, "Microsoft.Testing.Platform", StringComparison.Ordinal))
            ?.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(mtpVersion)) throw new InvalidOperationException("Microsoft.Testing.Platform version is unavailable");

        var xunitVersion = typeof(ITestResultMessage).Assembly.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(xunitVersion)) throw new InvalidOperationException("xUnit version is unavailable");

        return new ExecutionLedgerCapture(new ExecutionLedgerMetadata(
            Required(ExecutionLedgerEnvironment.RunId),
            manifestHash,
            manifestCount,
            assemblyPath,
            assemblySha256,
            xunitVersion,
            mtpVersion,
            parallelism,
            Required(ExecutionLedgerEnvironment.Path)));
    }

    public void RecordStarting(
        string uid,
        string name,
        string? className,
        string? collectionName,
        DateTimeOffset startTime)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("execution start identity is empty");
            if (starts.ContainsKey(uid) || results.Any(result => result.Uid == uid))
                throw new InvalidOperationException($"duplicate execution start for {uid}");
            starts.Add(uid, (name, className, collectionName, startTime));
        }
    }

    public void RecordResult(string uid, string outcome, decimal executionTimeSeconds, DateTimeOffset finishTime)
    {
        lock (gate)
        {
            if (!starts.Remove(uid, out var start))
                throw new InvalidOperationException($"execution result has no matching start for {uid}");
            if (outcome is not ("passed" or "failed" or "skipped" or "not-run"))
                throw new InvalidOperationException($"execution outcome is unsupported for {uid}");
            if (executionTimeSeconds < 0)
                throw new InvalidOperationException($"execution time is negative for {uid}");
            if (start.StartTime == DateTimeOffset.MinValue || finishTime == DateTimeOffset.MinValue || finishTime < start.StartTime)
                throw new InvalidOperationException($"execution timestamps are invalid for {uid}");
            results.Add(new ExecutionLedgerCase(uid, start.Name, outcome, executionTimeSeconds, start.StartTime, finishTime, start.ClassName, start.CollectionName));
        }
    }

    public ExecutionLedgerDocument ToDocument()
    {
        lock (gate)
        {
            if (starts.Count > 0)
                throw new InvalidOperationException("execution ledger has tests without results");
            return new ExecutionLedgerDocument
            {
                RunId = metadata.RunId,
                ManifestHash = metadata.ManifestHash,
                ManifestCount = metadata.ManifestCount,
                AssemblyPath = metadata.AssemblyPath,
                AssemblySha256 = metadata.AssemblySha256,
                XunitVersion = metadata.XunitVersion,
                MtpVersion = metadata.MtpVersion,
                Parallelism = metadata.Parallelism,
                Cases = results.OrderBy(result => result.Uid, StringComparer.Ordinal).ToArray(),
            };
        }
    }

    public string ToJson() => JsonSerializer.Serialize(ToDocument(), JsonOptions);

    public void WriteToFile()
    {
        var directory = Path.GetDirectoryName(metadata.OutputPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("execution ledger output path has no directory");
#pragma warning disable RS0030
        Directory.CreateDirectory(directory);
        File.WriteAllText(metadata.OutputPath, ToJson());
#pragma warning restore RS0030
    }
}
