using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Cli;

internal static class SkillAssetManifest
{
    public const string FileName = "manifest.json";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SkillAssetBuildIdentity ResolveCurrentBuildIdentity() =>
        ResolveBuildIdentity(
            informationalVersion: typeof(SkillAssetManifest).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            versionFromAssembly: typeof(SkillAssetManifest).Assembly.GetName().Version?.ToString(),
            getEnvHash: () => SystemEnvironmentVariableProvider.Instance.GetEnvironmentVariable(GitHashEnvironmentVariable),
            getGitHeadFromRepo: () => TryReadGitHeadFromAssembly(typeof(SkillAssetManifest).Assembly));

    public const string GitHashEnvironmentVariable = "MOHIST_GIT_HASH";

    public static SkillAssetBuildIdentity ResolveBuildIdentity(
        string? informationalVersion,
        string? versionFromAssembly,
        Func<string?> getEnvHash,
        Func<string?> getGitHeadFromRepo)
    {
        ArgumentNullException.ThrowIfNull(getEnvHash);
        ArgumentNullException.ThrowIfNull(getGitHeadFromRepo);

        string? version = versionFromAssembly;
        string? gitHash = null;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            if (plusIndex >= 0)
            {
                version = informationalVersion[..plusIndex];
                gitHash = informationalVersion[(plusIndex + 1)..];
            }
            else
            {
                version = informationalVersion;
            }
        }

        if (string.IsNullOrWhiteSpace(gitHash))
            gitHash = getEnvHash();

        if (string.IsNullOrWhiteSpace(gitHash))
            gitHash = getGitHeadFromRepo();

        return new SkillAssetBuildIdentity(
            string.IsNullOrWhiteSpace(version) ? null : version,
            string.IsNullOrWhiteSpace(gitHash) ? null : gitHash);
    }

    public static void Write(string assetRoot, SkillAssetBuildIdentity identity, IReadOnlyList<string> skillNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(skillNames);

        Write(assetRoot, identity, skillNames, RealFileSystem.Instance);
    }

    public static void Write(string assetRoot, SkillAssetBuildIdentity identity, IReadOnlyList<string> skillNames, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(skillNames);
        ArgumentNullException.ThrowIfNull(fileSystem);

        fileSystem.CreateDirectory(assetRoot);
        var normalized = skillNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var document = new SkillAssetManifestDocument(
            CurrentSchemaVersion,
            identity.Version,
            identity.GitHash,
            normalized);

        var manifestPath = Path.Combine(assetRoot, FileName);
        fileSystem.WriteAllText(manifestPath, JsonSerializer.Serialize(document, JsonOptions));
    }

    public static SkillAssetManifestReadResult TryRead(string assetRoot) =>
        TryRead(assetRoot, RealFileSystem.Instance);

    public static SkillAssetManifestReadResult TryRead(string assetRoot, IFileSystem fileSystem)
    {
        if (string.IsNullOrWhiteSpace(assetRoot))
            return SkillAssetManifestReadResult.Missing("Asset root was not provided.");

        var manifestPath = Path.Combine(assetRoot, FileName);
        if (!fileSystem.Exists(manifestPath))
            return SkillAssetManifestReadResult.Missing(
                $"Manifest file '{manifestPath}' is missing. Repair by running 'mo update' or 'scripts/install-mo.sh'.");

        string raw;
        try
        {
            raw = fileSystem.ReadAllText(manifestPath);
        }
        catch (Exception ex)
        {
            return SkillAssetManifestReadResult.Missing(
                $"Manifest file '{manifestPath}' could not be read: {ex.Message}. Repair by running 'mo update' or 'scripts/install-mo.sh'.");
        }

        SkillAssetManifestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SkillAssetManifestDocument>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            return SkillAssetManifestReadResult.Missing(
                $"Manifest file '{manifestPath}' is not valid JSON: {ex.Message}. Repair by running 'mo update' or 'scripts/install-mo.sh'.");
        }

        if (document is null)
            return SkillAssetManifestReadResult.Missing(
                $"Manifest file '{manifestPath}' is empty. Repair by running 'mo update' or 'scripts/install-mo.sh'.");

        return SkillAssetManifestReadResult.Loaded(new SkillAssetManifestData(
            document.SchemaVersion,
            string.IsNullOrWhiteSpace(document.CliVersion) ? null : document.CliVersion,
            string.IsNullOrWhiteSpace(document.GitHash) ? null : document.GitHash,
            document.Skills ?? []));
    }

    public static SkillAssetManifestValidation Validate(
        string assetRoot,
        SkillAssetBuildIdentity expectedIdentity,
        IReadOnlyList<string> expectedSkillNames) =>
        Validate(assetRoot, expectedIdentity, expectedSkillNames, RealFileSystem.Instance);

    public static SkillAssetManifestValidation Validate(
        string assetRoot,
        SkillAssetBuildIdentity expectedIdentity,
        IReadOnlyList<string> expectedSkillNames,
        IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ArgumentNullException.ThrowIfNull(expectedSkillNames);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var readResult = TryRead(assetRoot, fileSystem);
        if (!readResult.IsFound)
            return SkillAssetManifestValidation.Invalid(readResult.Error ?? $"Manifest is missing at '{assetRoot}'.");

        var manifest = readResult.Data!;
        var errors = new List<string>();
        var expectedSkillSet = new HashSet<string>(expectedSkillNames, StringComparer.Ordinal);
        var manifestSkills = manifest.Skills ?? [];

        if (manifest.SchemaVersion > CurrentSchemaVersion)
        {
            errors.Add(
                $"Manifest schema version {manifest.SchemaVersion} is newer than supported version {CurrentSchemaVersion}. " +
                "Repair by running 'mo update' or 'scripts/install-mo.sh'.");
        }

        if (!string.IsNullOrWhiteSpace(expectedIdentity.Version) && !string.IsNullOrWhiteSpace(manifest.Version))
        {
            if (!string.Equals(expectedIdentity.Version, manifest.Version, StringComparison.Ordinal))
            {
                errors.Add(
                    $"Manifest CLI version '{manifest.Version}' does not match running CLI version '{expectedIdentity.Version}'. " +
                    "Repair by running 'mo update' or 'scripts/install-mo.sh'.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(expectedIdentity.Version) && string.IsNullOrWhiteSpace(manifest.Version))
        {
            errors.Add(
                $"Manifest is missing CLI version (expected '{expectedIdentity.Version}'). " +
                "Repair by running 'mo update' or 'scripts/install-mo.sh'.");
        }

        if (!string.IsNullOrWhiteSpace(expectedIdentity.GitHash) && !string.IsNullOrWhiteSpace(manifest.GitHash))
        {
            if (!string.Equals(expectedIdentity.GitHash, manifest.GitHash, StringComparison.Ordinal))
            {
                errors.Add(
                    $"Manifest git hash '{manifest.GitHash}' does not match running CLI git hash '{expectedIdentity.GitHash}'. " +
                    "Repair by running 'mo update' or 'scripts/install-mo.sh'.");
            }
        }

        var manifestSkillSet = new HashSet<string>(manifestSkills, StringComparer.Ordinal);
        foreach (var expectedSkill in expectedSkillSet)
        {
            if (!manifestSkillSet.Contains(expectedSkill))
            {
                errors.Add(
                    $"Manifest does not list built-in skill '{expectedSkill}'. " +
                    "Repair by running 'mo update' or 'scripts/install-mo.sh'.");
            }
        }

        foreach (var skillName in manifestSkills)
        {
            var skillFile = Path.Combine(assetRoot, skillName, "SKILL.md");
            if (!fileSystem.Exists(skillFile))
            {
                errors.Add(
                    $"Manifest lists built-in skill '{skillName}' but '{skillFile}' is missing. " +
                    "Repair by running 'mo update' or 'scripts/install-mo.sh'.");
            }
        }

        return errors.Count == 0
            ? SkillAssetManifestValidation.Valid()
            : SkillAssetManifestValidation.Invalid(errors);
    }

    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000:Avoid accessing Assembly file path when publishing as a single file",
        Justification = "Empty Assembly.Location in single-file builds is handled by returning null.")]
    public static string? TryReadGitHeadFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var assemblyLocation = assembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyLocation))
            return null;

        var assemblyDir = Path.GetDirectoryName(assemblyLocation);
        if (string.IsNullOrWhiteSpace(assemblyDir))
            return null;

        try
        {
            var dir = new DirectoryInfo(assemblyDir);
            while (dir is not null)
            {
                var gitDir = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitDir))
                {
                    var headFile = Path.Combine(gitDir, "HEAD");
                    if (File.Exists(headFile))
                    {
                        var head = File.ReadAllText(headFile).Trim();
                        if (head.StartsWith("ref: ", StringComparison.Ordinal))
                        {
                            var refPath = head[5..];
                            var refFile = Path.Combine(gitDir, refPath);
                            if (File.Exists(refFile))
                                return File.ReadAllText(refFile).Trim();
                            return null;
                        }

                        return string.IsNullOrWhiteSpace(head) ? null : head;
                    }
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    internal sealed class SkillAssetManifestDocument
    {
        public SkillAssetManifestDocument(
            int schemaVersion,
            string? cliVersion,
            string? gitHash,
            IReadOnlyList<string> skills)
        {
            SchemaVersion = schemaVersion;
            CliVersion = cliVersion;
            GitHash = gitHash;
            Skills = skills;
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; }

        [JsonPropertyName("cliVersion")]
        public string? CliVersion { get; }

        [JsonPropertyName("gitHash")]
        public string? GitHash { get; }

        [JsonPropertyName("skills")]
        public IReadOnlyList<string> Skills { get; }
    }
}

internal sealed record SkillAssetBuildIdentity(string? Version, string? GitHash);

internal sealed record SkillAssetManifestData(
    int SchemaVersion,
    string? Version,
    string? GitHash,
    IReadOnlyList<string> Skills);

internal sealed record SkillAssetManifestReadResult(bool IsFound, string? Error, SkillAssetManifestData? Data)
{
    public static SkillAssetManifestReadResult Loaded(SkillAssetManifestData data) => new(true, null, data);

    public static SkillAssetManifestReadResult Missing(string error) => new(false, error, null);
}

internal sealed record SkillAssetManifestValidation(bool IsValid, IReadOnlyList<string> Errors)
{
    public static SkillAssetManifestValidation Valid() => new(true, Array.Empty<string>());

    public static SkillAssetManifestValidation Invalid(IReadOnlyList<string> errors) => new(false, errors);

    public static SkillAssetManifestValidation Invalid(string error) => new(false, new[] { error });

    public string? Summary => IsValid ? null : string.Join(" ", Errors);
}
