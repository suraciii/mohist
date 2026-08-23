namespace Mohist.Cli;

internal sealed record SlackServiceSnapshot(
    string Kind,
    string LaunchPath,
    string LaunchContent,
    string? MetadataPath = null,
    string? MetadataContent = null,
    bool MetadataExisted = false);

internal sealed record SlackUpdateRecoveryManifest(
    string Phase,
    bool HadPreviousBinary,
    string? PreviousBinarySha256,
    string BinaryName,
    string SnapshotId,
    bool WasNodeLauncher = false);
