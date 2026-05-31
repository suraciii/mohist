using System.Collections.Concurrent;

namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowBacklogDirectory
{
    void RegisterProject(string projectId);
    IReadOnlyList<string> ListProjects();
}

public sealed class InMemoryWorkflowBacklogDirectory : IWorkflowBacklogDirectory
{
    private readonly ConcurrentDictionary<string, byte> _projects = new(StringComparer.Ordinal);

    public void RegisterProject(string projectId)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
            _projects.TryAdd(projectId, 0);
    }

    public IReadOnlyList<string> ListProjects() => _projects.Keys.Order(StringComparer.Ordinal).ToArray();
}
