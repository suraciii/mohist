using System.Text;

namespace Mohist.Server.ArchTests;

internal sealed class SpecUnitMigrationGitProof
{
    internal const string ResourceName = "SpecUnitMigrationPr388GitProof.txt";
    private const string Commit = "602efa6abd6fca3efcd43b66b47ba10a80d9faba";
    private const string Parent = "0f723ddb87dfd0943b32e5b53b9af9ccbc89367d";
    private const string CommitTree = "9e9e05125716af8438f38db2984419cca73757d8";
    private const string ParentTree = "fd241bf9e42724d40843b75f485a35eaa56b2cfd";
    private const string ValidationHead = "2c96e43e2bc89fcfbd4e051576faec8f2861a8a8";
    private const string ValidationTree = "ff159609b97df5b1fac6d6404a6d1811f6bda99a";
    private const string ValidationBaselineDigest = "03cad8aca4e8ddf9688d7b3532d03cb1b93c039e8a9ec4e0e38ee73e5b403c62";

    private readonly IReadOnlyList<SpecUnitMigrationGitProofRawChange> _rawChanges;
    private readonly IReadOnlyList<SpecUnitMigrationGitProofMap> _maps;
    private readonly IReadOnlyDictionary<string, SpecUnitMigrationGitProofObject> _objects;
    private readonly IReadOnlyDictionary<string, SpecUnitMigrationGitProofSource> _sources;
    private readonly IReadOnlyDictionary<string, SpecUnitMigrationGitProofTarget> _targets;
    private readonly IReadOnlyList<SpecUnitMigrationGitProofCase> _cases;

    private SpecUnitMigrationGitProof(
        IReadOnlyList<SpecUnitMigrationGitProofRawChange> rawChanges,
        IReadOnlyList<SpecUnitMigrationGitProofMap> maps,
        IReadOnlyDictionary<string, SpecUnitMigrationGitProofObject> objects,
        IReadOnlyDictionary<string, SpecUnitMigrationGitProofSource> sources,
        IReadOnlyDictionary<string, SpecUnitMigrationGitProofTarget> targets,
        IReadOnlyList<SpecUnitMigrationGitProofCase> cases)
    {
        _rawChanges = rawChanges;
        _maps = maps;
        _objects = objects;
        _sources = sources;
        _targets = targets;
        _cases = cases;
    }

    internal IReadOnlyList<SpecUnitMigrationGitProofRawChange> RawChanges => _rawChanges;
    internal IReadOnlyList<SpecUnitMigrationGitProofCase> Cases => _cases;
    internal IReadOnlyList<SpecUnitMigrationGitProofChange> Changes
        => _maps.Select(map =>
        {
            var raw = _rawChanges.Single(change => change.SourcePath == map.SourcePath);
            var source = _sources[map.SourcePath];
            var target = _targets[map.TargetPath];
            return new SpecUnitMigrationGitProofChange(raw.Status, map.Operation,
                SpecUnitMigrationProvenance.NormalizeTestPath(map.SourcePath)!, source.Fqn,
                SpecUnitMigrationProvenance.NormalizeTestPath(map.TargetPath)!, target.Fqn, source.ContentDigest);
        }).ToArray();

    internal static SpecUnitMigrationGitProof Read()
        => Parse(ReadText());

    internal static string ReadText()
    {
        using var stream = typeof(SpecUnitMigrationLedgerRules).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static SpecUnitMigrationGitProof Parse(string text)
    {
        var raw = new List<SpecUnitMigrationGitProofRawChange>();
        var maps = new List<SpecUnitMigrationGitProofMap>();
        var objects = new Dictionary<string, SpecUnitMigrationGitProofObject>(StringComparer.Ordinal);
        var sources = new Dictionary<string, SpecUnitMigrationGitProofSource>(StringComparer.Ordinal);
        var targets = new Dictionary<string, SpecUnitMigrationGitProofTarget>(StringComparer.Ordinal);
        var cases = new List<SpecUnitMigrationGitProofCase>();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('|', StringSplitOptions.None);
            if (fields.Length == 2 && fields[0] is "version" or "commit" or "parent" or "tree" or "parent-tree"
                or "validation-head" or "validation-tree" or "validation-digest")
            {
                metadata[fields[0]] = fields[1];
                continue;
            }

            switch (fields[0])
            {
                case "raw" when fields.Length == 6:
                    raw.Add(new(fields[1], EmptyToNull(fields[2]), EmptyToNull(fields[3]), fields[4], fields[5]));
                    break;
                case "map" when fields.Length == 4:
                    maps.Add(new(fields[1], fields[2], fields[3]));
                    break;
                case "object" when fields.Length == 4:
                    objects.Add(ObjectKey(fields[1], fields[2]), new(fields[1], fields[2], fields[3]));
                    break;
                case "source" when fields.Length == 4:
                    sources.Add(fields[1], new(fields[1], fields[2], fields[3]));
                    break;
                case "target" when fields.Length == 3:
                    targets.Add(fields[1], new(fields[1], fields[2]));
                    break;
                case "case" when fields.Length >= 4:
                    cases.Add(new(fields[1], fields[2], string.Join("|", fields.Skip(3))));
                    break;
                default:
                    throw new InvalidOperationException($"Invalid PR #388 proof line: {line}");
            }
        }

        return new SpecUnitMigrationGitProof(raw, maps, objects, sources, targets, cases)
        {
            Metadata = metadata,
        };
    }

    private Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);

    internal IReadOnlyList<string> Validate()
    {
        var violations = new List<string>();
        Expect(Metadata, "version", "1", violations);
        Expect(Metadata, "commit", Commit, violations);
        Expect(Metadata, "parent", Parent, violations);
        Expect(Metadata, "tree", CommitTree, violations);
        Expect(Metadata, "parent-tree", ParentTree, violations);
        Expect(Metadata, "validation-head", ValidationHead, violations);
        Expect(Metadata, "validation-tree", ValidationTree, violations);
        Expect(Metadata, "validation-digest", ValidationBaselineDigest, violations);
        if (_rawChanges.Count != 46) violations.Add($"PR #388 raw object diff must contain 46 records, got {_rawChanges.Count}");
        if (_rawChanges.Count(change => change.Status.StartsWith("R", StringComparison.Ordinal)) != 40
            || _rawChanges.Count(change => change.Status == "D") != 2
            || _rawChanges.Count(change => change.Status == "M") != 3
            || _rawChanges.Count(change => change.Status == "A") != 1)
            violations.Add("PR #388 raw object diff must contain 40 renames, 2 deletes, 3 modifications, and 1 addition");
        if (_maps.Count != 42 || _maps.Select(map => map.SourcePath).Distinct(StringComparer.Ordinal).Count() != _maps.Count)
            violations.Add("PR #388 source-to-target map must contain 42 unique source paths");
        if (_sources.Count != 42 || _targets.Count != 42)
            violations.Add("PR #388 proof must contain 42 source and target endpoint records");

        foreach (var change in _rawChanges)
        {
            if (!ValidStatus(change.Status)) violations.Add($"invalid raw status {change.Status}");
            if (change.Status.StartsWith("R", StringComparison.Ordinal) && (change.SourcePath is null || change.TargetPath is null))
                violations.Add("rename raw record must preserve source and target paths");
            if (change.Status == "D" && (change.SourcePath is null || change.TargetPath is not null))
                violations.Add("delete raw record must have source and no raw target");
            if (change.Status == "M" && (change.SourcePath is null || change.TargetPath is null || change.SourcePath != change.TargetPath))
                violations.Add("modified raw record must bind the same parent and commit path");
            if (change.Status is "M" or "A" && change.TargetPath is null)
                violations.Add("non-delete raw record must have a target path");
            if (change.SourcePath is not null && (change.Status.StartsWith("R", StringComparison.Ordinal) || change.Status is "D" or "M"))
                CheckObject("parent", change.SourcePath, change.OldBlob, violations);
            if (change.TargetPath is not null && (change.Status.StartsWith("R", StringComparison.Ordinal) || change.Status is "M" or "A"))
                CheckObject("commit", change.TargetPath, change.NewBlob, violations);
        }

        foreach (var map in _maps)
        {
            var raw = _rawChanges.SingleOrDefault(change => change.SourcePath == map.SourcePath);
            if (raw is null) violations.Add($"mapped source is absent from raw diff: {map.SourcePath}");
            else if (raw.Status.StartsWith("R", StringComparison.Ordinal) && raw.TargetPath != map.TargetPath)
                violations.Add($"rename target differs from raw diff: {map.SourcePath}");
            if (!_sources.ContainsKey(map.SourcePath) || !_targets.ContainsKey(map.TargetPath))
                violations.Add($"mapped source/target object endpoint is missing: {map.SourcePath} -> {map.TargetPath}");
            CheckObject("parent", map.SourcePath, _objects.GetValueOrDefault(ObjectKey("parent", map.SourcePath))?.BlobId, violations);
            CheckObject("commit", map.TargetPath, _objects.GetValueOrDefault(ObjectKey("commit", map.TargetPath))?.BlobId, violations);
        }

        foreach (var source in _sources.Values)
            if (!IsDigest(source.ContentDigest)) violations.Add($"source content digest is invalid: {source.Path}");
        if (_cases.Count != 355 || _cases.Count(testCase => testCase.Kind == "moved") != 334
            || _cases.Count(testCase => testCase.Kind == "already-owned") != 21)
            violations.Add($"compiled case proof must contain 355/334/21 identities; actual={_cases.Count}/{_cases.Count(testCase => testCase.Kind == "moved")}/{_cases.Count(testCase => testCase.Kind == "already-owned")}");
        if (_cases.Any(testCase => testCase.Kind is not ("moved" or "already-owned"))
            || _cases.Select(testCase => testCase.Identity).Distinct(StringComparer.Ordinal).Count() != _cases.Count)
            violations.Add("compiled case proof kinds and identities must be an allowlisted unique set");
        return violations;
    }

    private void CheckObject(string side, string path, string? expected, ICollection<string> violations)
    {
        if (expected is null || !_objects.TryGetValue(ObjectKey(side, path), out var actual) || actual.BlobId != expected)
            violations.Add($"{side} blob object binding mismatch for {path}");
    }

    private static string ObjectKey(string side, string path) => $"{side}|{path}";
    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
    private static bool IsDigest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool ValidStatus(string status)
        => status is "M" or "A" or "D" || status.Length == 4 && status[0] == 'R' && status[1..].All(char.IsDigit);
    private static void Expect(IReadOnlyDictionary<string, string> metadata, string key, string expected, ICollection<string> violations)
    {
        if (!metadata.TryGetValue(key, out var actual) || actual != expected) violations.Add($"PR #388 proof {key} is not {expected}");
    }
}

internal sealed record SpecUnitMigrationGitProofRawChange(string Status, string? SourcePath, string? TargetPath, string OldBlob, string NewBlob);
internal sealed record SpecUnitMigrationGitProofMap(string Operation, string SourcePath, string TargetPath);
internal sealed record SpecUnitMigrationGitProofObject(string Side, string Path, string BlobId);
internal sealed record SpecUnitMigrationGitProofSource(string Path, string Fqn, string ContentDigest);
internal sealed record SpecUnitMigrationGitProofTarget(string Path, string Fqn);
internal sealed record SpecUnitMigrationGitProofCase(string Kind, string Fqn, string Identity);
internal sealed record SpecUnitMigrationGitProofChange(
    string RawStatus,
    string Operation,
    string SourcePath,
    string SourceFqn,
    string TargetPath,
    string TargetFqn,
    string SourceContentDigest);
