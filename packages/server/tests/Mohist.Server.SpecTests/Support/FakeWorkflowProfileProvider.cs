using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Minimal <see cref="IWorkflowProfileProvider"/> for direct-construction
/// specs. Lets the spec override the resolution answers it cares about; the
/// default answers return the empty/null/unsupported shape the provider's
/// real callers already see when the project has no Profile configured.
///
/// Specs that exercise the coordinator's binding participant proxy, the
/// workflow-profile reference coordinator, or the workflow-profile manager
/// against a project that owns profile data should construct a real
/// <see cref="WorkflowProfileProvider"/> with a migrated test database
/// instead; this fake exists so specs that only need the provider in scope
/// to keep its consumer's constructor happy do not have to bootstrap a
/// full Profile collection.
/// </summary>
public sealed class FakeWorkflowProfileProvider : IWorkflowProfileProvider
{
    private readonly Dictionary<string, HashSet<string>> _known = new(StringComparer.Ordinal);

    public FakeWorkflowProfileProvider Add(string projectId, params string[] profileIds)
    {
        if (!_known.TryGetValue(projectId, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _known[projectId] = set;
        }
        foreach (var id in profileIds)
            set.Add(id);
        return this;
    }

    public Task<IReadOnlyList<WorkflowProfileCollectionEntry>> ListAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkflowProfileCollectionEntry>>([]);

    public Task<WorkflowProfileCollectionEntry?> GetAsync(string projectId, string profileId, CancellationToken ct = default) =>
        Task.FromResult<WorkflowProfileCollectionEntry?>(null);

    public Task<WorkflowDefinition?> GetDefinitionAsync(string projectId, string profileId, CancellationToken ct = default) =>
        Task.FromResult<WorkflowDefinition?>(null);

    public Task<string?> GetDefinitionSourceAsync(string projectId, string profileId, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<WorkflowProfileSourceProvenance?> GetSourceProvenanceAsync(string projectId, string profileId, CancellationToken ct = default) =>
        Task.FromResult<WorkflowProfileSourceProvenance?>(null);

    public Task<WorkflowProfileSaveResult> CreateAsync(string projectId, WorkflowProfileCollectionEntry request, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeWorkflowProfileProvider does not support Create; use the real WorkflowProfileProvider in a migrated test database.");

    public Task<WorkflowProfileSaveResult> UpdateAsync(string projectId, WorkflowProfileCollectionEntry request, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeWorkflowProfileProvider does not support Update; use the real WorkflowProfileProvider in a migrated test database.");

    public Task<bool> DeleteAsync(string projectId, string profileId, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeWorkflowProfileProvider does not support Delete; use the real WorkflowProfileProvider in a migrated test database.");

    public Task<bool> ContainsAsync(string projectId, string profileId, CancellationToken ct = default)
    {
        var known = _known.TryGetValue(projectId, out var set) && set.Contains(profileId);
        return Task.FromResult(known);
    }
}
