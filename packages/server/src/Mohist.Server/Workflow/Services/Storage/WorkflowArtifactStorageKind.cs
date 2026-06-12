namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Storage shape of a recorded workflow artifact. File artifacts persist a
/// single <c>content</c> file alongside <c>metadata.json</c>; directory
/// artifacts persist the contained tree under <c>files/</c> alongside
/// <c>metadata.json</c>. The values here mirror the string persisted in
/// <c>WorkflowArtifactRow.Kind</c>.
/// </summary>
public enum WorkflowArtifactStorageKind
{
    File,
    Directory,
}
