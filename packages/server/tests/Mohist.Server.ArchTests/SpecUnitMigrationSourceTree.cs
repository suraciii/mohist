using System.Security.Cryptography;
using System.Text;

namespace Mohist.Server.ArchTests;

internal sealed record SpecUnitMigrationSourceTreeSnapshot(
    string ValidationHead,
    string ValidationTree,
    int FileCount,
    string Digest);

internal static class SpecUnitMigrationSourceTree
{
    internal static SpecUnitMigrationSourceTreeSnapshot Capture(
        IEnumerable<ArchitectureRules.EmbeddedSource> sources,
        string validationHead,
        string validationTree)
    {
        var files = sources
            .AsParallel()
            .WithDegreeOfParallelism(SpecUnitMigrationInventory.ProofParallelism)
            .Where(source => source.Path.EndsWith(".cs", StringComparison.Ordinal)
                && (source.Path.StartsWith("Mohist.Server.SpecTests/", StringComparison.Ordinal)
                    || source.Path.StartsWith("Mohist.Server.UnitTests/", StringComparison.Ordinal)
                    || source.Path.StartsWith("Mohist.Server.TestSupport/", StringComparison.Ordinal)
                    || source.Path == "eng/TestTime.cs"))
            .Select(source => $"{source.Path}|{source.ByteLength}|{ContentDigest(source.Content)}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            [$"validation-head={validationHead}", $"validation-tree={validationTree}", .. files])))).ToLowerInvariant();
        return new SpecUnitMigrationSourceTreeSnapshot(validationHead, validationTree, files.Length, digest);
    }

    private static string ContentDigest(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
