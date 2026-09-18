using System.Security.Cryptography;

namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Single source of truth for the durable content-hash string format
/// (<c>sha256:&lt;lower-hex&gt;</c>) recorded in artifact metadata and
/// compared against declared entry hashes. Both the filesystem and
/// in-memory storages use it so their durable manifests cannot drift.
/// </summary>
internal static class WorkflowArtifactContentHash
{
    public const string Algorithm = "sha256";

    public static string Complete(IncrementalHash hash) =>
        Format(hash.GetHashAndReset());

    public static string Format(byte[] digest) =>
        $"{Algorithm}:{Convert.ToHexString(digest).ToLowerInvariant()}";
}
