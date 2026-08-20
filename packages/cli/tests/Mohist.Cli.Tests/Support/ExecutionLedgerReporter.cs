using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
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

    public string? RunnerSwitch => "mohist-ledger";

    public ValueTask<IRunnerReporterMessageHandler> CreateMessageHandler(IRunnerLogger logger, IMessageSink? diagnosticMessageSink) =>
        new(new ExecutionLedgerReporterMessageHandler(logger));
}

internal static class ExecutionLedgerEnvironment
{
    public const string Path = "MOHIST_EXECUTION_LEDGER_PATH";
    public const string RunId = "MOHIST_EXECUTION_LEDGER_RUN_ID";
    public const string ManifestHash = "MOHIST_EXECUTION_LEDGER_MANIFEST_HASH";
    public const string ManifestCount = "MOHIST_EXECUTION_LEDGER_MANIFEST_COUNT";
    public const string AssemblyPath = "MOHIST_EXECUTION_LEDGER_ASSEMBLY_PATH";
    public const string AssemblySha256 = "MOHIST_EXECUTION_LEDGER_ASSEMBLY_SHA256";
    public const string SourceSha256 = "MOHIST_EXECUTION_LEDGER_SOURCE_SHA256";
    public const string Parallelism = "MOHIST_EXECUTION_LEDGER_PARALLELISM";
}

internal interface IExecutionLedgerRuntime
{
    string AssemblyPath { get; }
    string? XunitVersion { get; }
    string? MtpVersion { get; }
    string? GetEnvironmentVariable(string name);
}

internal interface IExecutionLedgerArtifactStore
{
    byte[] ReadAllBytes(string path);
    void WriteAllText(string path, string content);
}

internal sealed class SystemExecutionLedgerRuntime : IExecutionLedgerRuntime
{
    public static SystemExecutionLedgerRuntime Instance { get; } = new();

    public string AssemblyPath => typeof(ExecutionLedgerReporter).Assembly.Location;
    public string? XunitVersion => typeof(ITestResultMessage).Assembly.GetName().Version?.ToString();
    public string? MtpVersion => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, "Microsoft.Testing.Platform", StringComparison.Ordinal))
        ?.GetName().Version?.ToString();

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);
}

internal sealed class PhysicalExecutionLedgerArtifactStore : IExecutionLedgerArtifactStore
{
    public static PhysicalExecutionLedgerArtifactStore Instance { get; } = new();

    public byte[] ReadAllBytes(string path)
    {
#pragma warning disable RS0030
        return File.ReadAllBytes(path);
#pragma warning restore RS0030
    }

    public void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("execution ledger output path has no directory");
#pragma warning disable RS0030
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, content);
#pragma warning restore RS0030
    }
}

public sealed class ExecutionLedgerReporterMessageHandler : IRunnerReporterMessageHandler
{
    private readonly ExecutionLedgerCapture capture;
    private readonly IExecutionLedgerArtifactStore artifacts;
    private readonly IRunnerReporterMessageHandler? output;
    private readonly object metadataGate = new();
    private MessageMetadataCache metadata = new();
    private int cachedMetadataCount;
    private string? error;

    public ExecutionLedgerReporterMessageHandler(IRunnerLogger logger) : this(
        SystemExecutionLedgerRuntime.Instance,
        PhysicalExecutionLedgerArtifactStore.Instance,
        new DefaultRunnerReporterMessageHandler(logger))
    { }

    internal ExecutionLedgerReporterMessageHandler(
        IExecutionLedgerRuntime runtime,
        IExecutionLedgerArtifactStore artifacts)
        : this(runtime, artifacts, null)
    { }

    private ExecutionLedgerReporterMessageHandler(
        IExecutionLedgerRuntime runtime,
        IExecutionLedgerArtifactStore artifacts,
        IRunnerReporterMessageHandler? output)
    {
        this.artifacts = artifacts;
        this.output = output;
        capture = ExecutionLedgerCapture.FromEnvironment(runtime, artifacts);
    }

    internal int CachedMetadataCount
    {
        get { lock (metadataGate) return cachedMetadataCount; }
    }

    public bool OnMessage(IMessageSinkMessage message)
    {
        var shouldContinue = output?.OnMessage(message) ?? true;
        try
        {
            CacheMetadata(message);

            if (message is ITestStarting starting && message is ITestMetadata testMetadata && message is ITestMessage testMessage)
            {
                var (className, collectionName) = GetMetadata(message);
                capture.RecordStarting(
                    testMessage.TestUniqueID,
                    testMessage.TestCaseUniqueID,
                    testMetadata.TestDisplayName,
                    className,
                    collectionName,
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
        finally
        {
            try
            {
                ReleaseMetadata(message);
            }
            catch (Exception exception)
            {
                error ??= exception.ToString();
            }
        }

        return shouldContinue;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (error is null)
                capture.WriteTo(artifacts);
        }
        catch
        {
            // A missing or invalid ledger is fail-closed in the duration guard.
        }
        finally
        {
            lock (metadataGate)
            {
                metadata = new MessageMetadataCache();
                cachedMetadataCount = 0;
            }
            if (output is not null)
                await output.DisposeAsync();
        }
    }

    private (string? ClassName, string? CollectionName) GetMetadata(IMessageSinkMessage message)
    {
        lock (metadataGate)
        {
            return (
                message is ITestCaseMessage caseMessage ? metadata.TryGetTestCaseMetadata(caseMessage)?.TestClassName : null,
                message is ITestCollectionMessage collectionMessage ? metadata.TryGetCollectionMetadata(collectionMessage)?.TestCollectionDisplayName : null);
        }
    }

    private void CacheMetadata(IMessageSinkMessage message)
    {
        lock (metadataGate)
        {
            if (message is ITestAssemblyStarting assemblyStarting) { metadata.Set(assemblyStarting); cachedMetadataCount++; }
            if (message is ITestCaseStarting caseStarting) { metadata.Set(caseStarting); cachedMetadataCount++; }
            if (message is ITestClassStarting classStarting) { metadata.Set(classStarting); cachedMetadataCount++; }
            if (message is ITestCollectionStarting collectionStarting) { metadata.Set(collectionStarting); cachedMetadataCount++; }
            if (message is ITestMethodStarting methodStarting) { metadata.Set(methodStarting); cachedMetadataCount++; }
            if (message is ITestStarting testStarting) { metadata.Set(testStarting); cachedMetadataCount++; }
        }
    }

    private void ReleaseMetadata(IMessageSinkMessage message)
    {
        lock (metadataGate)
        {
            object? removed = message switch
            {
                ITestAssemblyFinished finished => metadata.TryRemove(finished),
                ITestCaseFinished finished => metadata.TryRemove(finished),
                ITestClassFinished finished => metadata.TryRemove(finished),
                ITestCollectionFinished finished => metadata.TryRemove(finished),
                ITestMethodFinished finished => metadata.TryRemove(finished),
                ITestFinished finished => metadata.TryRemove(finished),
                _ => null,
            };
            if (removed is not null) cachedMetadataCount--;
        }
    }
}

public sealed record ExecutionLedgerMetadata(
    string RunId,
    string ManifestHash,
    int ManifestCount,
    string AssemblyPath,
    string AssemblySha256,
    string SourceSha256,
    string XunitVersion,
    string MtpVersion,
    string Parallelism,
    string OutputPath);

public sealed record ExecutionLedgerCase(
    string Uid,
    string TestCaseUid,
    string Name,
    string Outcome,
    decimal ExecutionTimeSeconds,
    DateTimeOffset StartTime,
    DateTimeOffset FinishTime,
    string? ClassName,
    string? CollectionName);

public sealed class ExecutionLedgerDocument
{
    public int SchemaVersion { get; init; } = 2;
    public string RunId { get; init; } = string.Empty;
    public string ManifestHash { get; init; } = string.Empty;
    public int ManifestCount { get; init; }
    public string AssemblyPath { get; init; } = string.Empty;
    public string AssemblySha256 { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
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
    private readonly Dictionary<string, (string TestCaseUid, string Name, string? ClassName, string? CollectionName, DateTimeOffset StartTime)> starts = [];
    private readonly List<ExecutionLedgerCase> results = [];

    public ExecutionLedgerCapture(ExecutionLedgerMetadata metadata)
    {
        this.metadata = metadata;
    }

    internal static ExecutionLedgerCapture FromEnvironment(
        IExecutionLedgerRuntime runtime,
        IExecutionLedgerArtifactStore artifacts)
    {
        string Required(string name) =>
            runtime.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"missing execution ledger environment variable {name}");

        var assemblyPath = runtime.AssemblyPath;
        var expectedAssemblyPath = Required(ExecutionLedgerEnvironment.AssemblyPath);
        if (!string.Equals(assemblyPath, expectedAssemblyPath, StringComparison.Ordinal))
            throw new InvalidOperationException("execution ledger assembly path does not match the current test assembly");

        var assemblySha256 = Convert.ToHexString(SHA256.HashData(artifacts.ReadAllBytes(assemblyPath))).ToLowerInvariant();
        if (!string.Equals(assemblySha256, Required(ExecutionLedgerEnvironment.AssemblySha256), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("execution ledger assembly hash does not match the current test assembly");

        if (!int.TryParse(Required(ExecutionLedgerEnvironment.ManifestCount), out var manifestCount) || manifestCount <= 0)
            throw new InvalidOperationException("execution ledger manifest count is invalid");
        var manifestHash = Required(ExecutionLedgerEnvironment.ManifestHash);
        if (manifestHash.Length != 64 || manifestHash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("execution ledger manifest hash is invalid");
        var parallelism = Required(ExecutionLedgerEnvironment.Parallelism);
        var sourceSha256 = Required(ExecutionLedgerEnvironment.SourceSha256);
        if (sourceSha256.Length != 64 || sourceSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("execution ledger source hash is invalid");

        var mtpVersion = runtime.MtpVersion;
        if (string.IsNullOrWhiteSpace(mtpVersion)) throw new InvalidOperationException("Microsoft.Testing.Platform version is unavailable");

        var xunitVersion = runtime.XunitVersion;
        if (string.IsNullOrWhiteSpace(xunitVersion)) throw new InvalidOperationException("xUnit version is unavailable");

        return new ExecutionLedgerCapture(new ExecutionLedgerMetadata(
            Required(ExecutionLedgerEnvironment.RunId),
            manifestHash,
            manifestCount,
            assemblyPath,
            assemblySha256,
            sourceSha256,
            xunitVersion,
            mtpVersion,
            parallelism,
            Required(ExecutionLedgerEnvironment.Path)));
    }

    public void RecordStarting(
        string uid,
        string testCaseUid,
        string name,
        string? className,
        string? collectionName,
        DateTimeOffset startTime)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(testCaseUid) || string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("execution start identity is empty");
            if (starts.ContainsKey(uid) || results.Any(result => result.Uid == uid))
                throw new InvalidOperationException($"duplicate execution start for {uid}");
            if (starts.Values.Any(start => start.Name == name) || results.Any(result => result.Name == name))
                throw new InvalidOperationException($"duplicate execution test name {name}");
            starts.Add(uid, (testCaseUid, name, className, collectionName, startTime));
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
            results.Add(new ExecutionLedgerCase(uid, start.TestCaseUid, start.Name, outcome, executionTimeSeconds, start.StartTime, finishTime, start.ClassName, start.CollectionName));
        }
    }

    public ExecutionLedgerDocument ToDocument()
    {
        lock (gate)
        {
            if (starts.Count > 0)
                throw new InvalidOperationException("execution ledger has tests without results");
            if (results.Count == 0)
                throw new InvalidOperationException("execution ledger has no test results");
            return new ExecutionLedgerDocument
            {
                RunId = metadata.RunId,
                ManifestHash = metadata.ManifestHash,
                ManifestCount = metadata.ManifestCount,
                AssemblyPath = metadata.AssemblyPath,
                AssemblySha256 = metadata.AssemblySha256,
                SourceSha256 = metadata.SourceSha256,
                XunitVersion = metadata.XunitVersion,
                MtpVersion = metadata.MtpVersion,
                Parallelism = metadata.Parallelism,
                Cases = results.OrderBy(result => result.Uid, StringComparer.Ordinal).ToArray(),
            };
        }
    }

    public string ToJson() => JsonSerializer.Serialize(ToDocument(), JsonOptions);

    internal void WriteTo(IExecutionLedgerArtifactStore artifacts) =>
        artifacts.WriteAllText(metadata.OutputPath, ToJson());
}
