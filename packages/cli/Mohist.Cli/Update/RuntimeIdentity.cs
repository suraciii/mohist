using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mohist.Cli;

internal sealed record UpdateSourceIdentity(
    string RepositoryRoot,
    bool ExplicitRoot,
    string GitCommit,
    string TreeHash,
    string SourceDigest)
{
    public string Version => $"0.0.0+{GitCommit}";

    public string ReleaseId(string scope) =>
        string.Equals(scope, "full", StringComparison.Ordinal)
            ? $"mohist-full-{GitCommit}"
            : $"mohist-{scope}-{GitCommit}";

    public static string ComputeDigest(string gitCommit, string treeHash)
    {
        var bytes = Encoding.UTF8.GetBytes($"git\n{gitCommit}\ntree\n{treeHash}\n");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

internal sealed record RuntimeIdentity(
    string Component,
    string Version,
    string SourceRevision,
    string TreeHash,
    string ArtifactDigest,
    string ReleaseId,
    long Generation,
    string? RunnerId = null,
    string? ConnectionGeneration = null,
    string? BuildGitHash = null)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Component)
        && !string.IsNullOrWhiteSpace(Version)
        && !string.IsNullOrWhiteSpace(SourceRevision)
        && !string.IsNullOrWhiteSpace(TreeHash)
        && !string.IsNullOrWhiteSpace(ArtifactDigest)
        && !string.IsNullOrWhiteSpace(ReleaseId)
        && Generation > 0
        && (!string.Equals(Component, "runner", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(RunnerId));

    public bool Matches(RuntimeIdentity expected)
    {
        if (!IsComplete || !expected.IsComplete)
            return false;

        return Differences(expected).Count == 0;
    }

    public IReadOnlyList<RuntimeIdentityDifference> Differences(RuntimeIdentity expected)
    {
        var differences = new List<RuntimeIdentityDifference>();
        Add(differences, "component", expected.Component, Component);
        Add(differences, "version", expected.Version, Version);
        Add(differences, "buildGitHash", expected.BuildGitHash, BuildGitHash, expected.BuildGitHash is not null);
        Add(differences, "sourceRevision", expected.SourceRevision, SourceRevision);
        Add(differences, "treeHash", expected.TreeHash, TreeHash);
        Add(differences, "artifactDigest", expected.ArtifactDigest, ArtifactDigest);
        Add(differences, "releaseId", expected.ReleaseId, ReleaseId);
        Add(differences, "generation", expected.Generation.ToString(), Generation.ToString());
        Add(differences, "runnerId", expected.RunnerId, RunnerId, expected.RunnerId is not null);
        return differences;
    }

    private static void Add(
        ICollection<RuntimeIdentityDifference> differences,
        string field,
        string? expected,
        string? actual,
        bool required = true)
    {
        if (required && !string.Equals(expected, actual, StringComparison.Ordinal))
            differences.Add(new RuntimeIdentityDifference(field, expected, actual));
    }

    public static RuntimeIdentity? Read(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<RuntimeIdentity>(json, JsonOptions);
            return value is { IsComplete: true } ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}

internal sealed record RunnerLaunchIdentity(string RunnerId)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(RunnerId);
}

internal sealed record RuntimeIdentityDifference(string Field, string? Expected, string? Actual)
{
    public override string ToString() =>
        $"{Field} expected='{Expected ?? "<null>"}' actual='{Actual ?? "<null>"}'";
}

internal static class ManagedRuntimeLayout
{
    public const string CliEntrypoint = "Mohist.Cli";
    public const string ServerEntrypoint = "Mohist.Server";
    public const string RunnerEntrypoint = "dist/cli.js";
    public const string RunnerBuildInfo = "dist/build-info.json";

    public static string EntrypointFor(string component) => component switch
    {
        "cli" => CliEntrypoint,
        "server" => ServerEntrypoint,
        "runner" => RunnerEntrypoint,
        _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unknown managed runtime component."),
    };
}

internal sealed record RuntimeTarget(
    string Component,
    string Entrypoint,
    string WorkingDirectory,
    string[] Arguments,
    string RuntimeIdentifier,
    RuntimeIdentity Identity,
    string? NodeExecutable = null,
    string? DependencyRoot = null,
    RuntimeLaunchMode LaunchMode = RuntimeLaunchMode.SelfContained)
{
    public bool IsAbsoluteTarget =>
        Path.IsPathRooted(Entrypoint)
        && Path.IsPathRooted(WorkingDirectory)
        && (NodeExecutable is null || Path.IsPathRooted(NodeExecutable))
        && (DependencyRoot is null || Path.IsPathRooted(DependencyRoot))
        && (LaunchMode != RuntimeLaunchMode.Node
            || NodeExecutable is not null && Path.IsPathRooted(NodeExecutable));

    public bool UsesCanonicalEntrypoint =>
        Component != "runner"
        || string.Equals(
            Path.Combine(WorkingDirectory, ManagedRuntimeLayout.RunnerEntrypoint).Replace('\\', '/'),
            Entrypoint,
            StringComparison.Ordinal);
}

internal enum RuntimeLaunchMode
{
    SelfContained,
    Node,
}

internal sealed record ManagedUnitSnapshot(
    string UnitName,
    bool Exists,
    byte[]? Contents,
    bool WasActive,
    bool WasEnabled);

internal sealed record ManagedRuntimeSnapshot(
    ManagedUnitSnapshot? Server,
    ManagedUnitSnapshot? Runner);

internal enum ManagedRuntimeRestoreState
{
    NotAttempted,
    Restored,
    Failed,
}

internal sealed record ManagedRuntimeRestoreResult(
    int ExitCode,
    ManagedRuntimeRestoreState Server,
    ManagedRuntimeRestoreState Runner,
    string? Diagnostic)
{
    public ManagedRuntimeRecovery ToRecovery(string fallbackDiagnostic) =>
        new(
            Server.ToString(),
            Runner.ToString(),
            string.IsNullOrWhiteSpace(Diagnostic) ? fallbackDiagnostic : Diagnostic);

    public static ManagedRuntimeRestoreResult FromExitCode(int exitCode, string scope, string? diagnostic = null) =>
        new(
            exitCode,
            Includes(scope, "server") ? exitCode == 0 ? ManagedRuntimeRestoreState.Restored : ManagedRuntimeRestoreState.Failed : ManagedRuntimeRestoreState.NotAttempted,
            Includes(scope, "runner") ? exitCode == 0 ? ManagedRuntimeRestoreState.Restored : ManagedRuntimeRestoreState.Failed : ManagedRuntimeRestoreState.NotAttempted,
            diagnostic);

    private static bool Includes(string scope, string component) =>
        string.Equals(scope, "full", StringComparison.Ordinal)
        || string.Equals(scope, component, StringComparison.Ordinal);
}

internal sealed record ManagedRuntimeRecovery(
    string Server,
    string Runner,
    string Diagnostic);

internal sealed record RuntimeTargetSet(
    string Status,
    long Generation,
    string TransactionId,
    RuntimeTarget? Cli,
    RuntimeTarget? Server,
    RuntimeTarget? Runner,
    RuntimeTargetSet? Previous,
    string? ActivationLease = null,
    ManagedRuntimeSnapshot? SourceSnapshot = null,
    string? RecoveryDiagnostic = null,
    ManagedRuntimeRecovery? Recovery = null)
{
    public bool IsNone => string.Equals(Status, "none", StringComparison.Ordinal);

    public bool IsCompleteFor(string scope)
    {
        if (string.Equals(scope, "full", StringComparison.Ordinal))
            return Cli is not null && Server is not null && Runner is not null;
        if (string.Equals(scope, "cli", StringComparison.Ordinal))
            return Cli is not null;
        if (string.Equals(scope, "server", StringComparison.Ordinal))
            return Server is not null;
        if (string.Equals(scope, "runner", StringComparison.Ordinal))
            return Runner is not null;
        return false;
    }
}

internal sealed record UpdateSourceContext(
    UpdateSourceIdentity Source,
    string SnapshotRoot,
    string BuildWorkspaceRoot,
    string CandidateRoot,
    string RuntimeRoot,
    string TransactionId,
    string Scope,
    string? CliPath)
{
    public string ReleaseId => Source.ReleaseId(Scope);
    public string Version => Source.Version;
}

internal sealed record ManagedUpdateResult(
    bool Success,
    int ExitCode,
    string Stage,
    string Recovery,
    RuntimeTargetSet? ActiveTargets,
    RuntimeIdentity? ExpectedIdentity,
    string? ObservedIdentity,
    string? Error)
{
    public static ManagedUpdateResult Failed(
        int exitCode,
        string stage,
        string recovery,
        string? error,
        RuntimeTargetSet? activeTargets = null,
        RuntimeIdentity? expectedIdentity = null,
        string? observedIdentity = null) =>
        new(false, exitCode == 0 ? 1 : exitCode, stage, recovery, activeTargets, expectedIdentity, observedIdentity, error);
}
