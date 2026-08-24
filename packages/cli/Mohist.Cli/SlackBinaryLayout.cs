namespace Mohist.Cli;

internal static class SlackBinaryLayout
{
    internal static string BuildArtifact(string repoRoot, string binaryName) =>
        Path.Combine(repoRoot, "packages", "go", "mohist-slack", "bin", "build", binaryName);

    internal static string InstalledBinary(string repoRoot, string binaryName) =>
        Path.Combine(repoRoot, "packages", "go", "mohist-slack", "bin", binaryName);

    internal static string TransactionLock(string userHome) =>
        Path.Combine(userHome, ".mohist", "update", "slack", "transaction.lock");

    internal static string GlobalRecoveryMarker(string userHome) =>
        Path.Combine(userHome, ".mohist", "update", "slack", "recovery-required");

    internal static string UpdateStagingDirectory(string repoRoot) =>
        Path.Combine(repoRoot, "packages", "go", "mohist-slack", "bin", ".update");

    internal static int PromoteBuildArtifact(
        string repoRoot,
        string binaryName,
        bool dryRun,
        IFileSystem fileSystem,
        TextWriter output,
        TextWriter error)
    {
        var source = BuildArtifact(repoRoot, binaryName);
        var destination = InstalledBinary(repoRoot, binaryName);
        if (dryRun)
        {
            output.WriteLine($"Dry run: would promote {source} to {destination}");
            return 0;
        }
        if (!fileSystem.Exists(source))
        {
            error.WriteLine($"Slack build artifact not found: {source}. Run 'npm run build:slack' first.");
            return 1;
        }
        var directory = Path.GetDirectoryName(destination)!;
        if (!fileSystem.DirectoryExists(directory)) fileSystem.CreateDirectory(directory);
        var temp = $"{destination}.install.tmp";
        try
        {
            fileSystem.CopyFile(source, temp);
            fileSystem.MoveFile(temp, destination);
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Slack binary installation failed: {ex.Message}");
            return 1;
        }
    }
}
