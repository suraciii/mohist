using Mohist.Server.Infrastructure.Orleans;

namespace Mohist.Server.Infrastructure.Orleans;

/// <summary>
/// Orleans grain-key factory methods. New code MUST use the typed
/// <see cref="IssueKey"/> / <see cref="EpicKey"/> entry points so that the
/// grain-key format is owned by one place (<see cref="ScopedGrainKeyCodec"/>).
/// The legacy <see cref="Issue(string)"/> form keys an Issue by random id and
/// is preserved only for the temporary migration sequence of issue #412; it
/// is removed by T-002 once all call sites route through the typed key.
/// </summary>
public static class GrainKey
{
    /// <summary>
    /// Canonical Orleans grain key for an Issue given its Project-scoped
    /// identity. Routes through <see cref="ScopedGrainKeyCodec"/>.
    /// </summary>
    public static string Issue(string projectId, int issueNumber) =>
        ScopedGrainKeyCodec.Format(projectId, issueNumber);

    public static string Issue(IssueKey key) =>
        ScopedGrainKeyCodec.Format(key.ProjectId, key.IssueNumber);

    /// <summary>
    /// Canonical Orleans grain key for an Epic given its Project-scoped
    /// identity. Routes through <see cref="ScopedGrainKeyCodec"/>.
    /// </summary>
    public static string Epic(string projectId, int epicNumber) =>
        ScopedGrainKeyCodec.Format(projectId, epicNumber);

    public static string Epic(EpicKey key) =>
        ScopedGrainKeyCodec.Format(key.ProjectId, key.EpicNumber);

    public static string Agent(string projectId, string agentId) => $"{projectId}:{agentId}";
    public static string IssueCounter(string projectId) => projectId;
    public static string EpicCounter(string projectId) => projectId;
    public static string WorkflowBacklog(string projectId) => projectId;

    [Obsolete("Runner registries are global only; use RunnerRegistryKeys.Global.", error: false)]
    public static string RunnerRegistry(string projectId) => projectId;

    /// <summary>
    /// Temporary migration-sequence entry point preserved for the issue #412
    /// T-001/T-002 transition: keys an Issue by its legacy random id so the
    /// existing grain activation path keeps compiling until every call site
    /// is rewritten to use the Project-scoped key. Removed by T-002.
    /// </summary>
    public static string Issue(string issueId) => issueId;
}
