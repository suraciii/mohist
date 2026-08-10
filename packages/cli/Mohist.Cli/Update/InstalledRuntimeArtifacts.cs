using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mohist.Cli;

internal enum ManagedRuntimeComponent
{
    Server,
    Runner,
}

internal sealed record UpdateSource(string Root, string Hash);

internal sealed record RuntimeArtifactIdentity(
    string SourceHash,
    string ArtifactDigest,
    string EntryPoint,
    IReadOnlyList<string> PayloadFiles);

internal sealed record InstalledRuntimeArtifact(
    ManagedRuntimeComponent Component,
    string SourceHash,
    string ComponentRoot,
    string VersionRoot,
    string ArtifactDigest,
    string EntryPoint)
{
    public string CurrentLink => Path.Combine(ComponentRoot, "current");
    public string VerifiedLink => Path.Combine(ComponentRoot, "verified");
}

internal sealed record RuntimeActivation(
    InstalledRuntimeArtifact Candidate,
    InstalledRuntimeArtifact? PreviousCurrent,
    InstalledRuntimeArtifact? PreviousVerified);

internal sealed record RuntimeRecovery(
    bool Restored,
    InstalledRuntimeArtifact? RestoredCurrent,
    InstalledRuntimeArtifact? RestoredVerified,
    string? Failure = null);

internal sealed class RuntimeActivationException(
    RuntimeActivation activation,
    string message) : InvalidOperationException(message)
{
    public RuntimeActivation Activation { get; } = activation;
}

/// <summary>
/// Owns the source-to-installed-artifact boundary for managed Server and Runner services.
/// A service may only execute a version under this root; source checkouts are build inputs.
/// </summary>
internal sealed class InstalledRuntimeArtifacts
{
    private const string ManifestFileName = "mohist-build.json";
    private const string RunnerBuildInfoPath = "dist/build-info.json";

    private readonly TextWriter _error;
    private readonly ICommandExecutor _commands;
    private readonly IFileSystem _files;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly Func<string?>? _getUserHome;

    public InstalledRuntimeArtifacts(
        TextWriter error,
        ICommandExecutor commands,
        IFileSystem files,
        IEnvironmentVariableProvider environment,
        Func<string?>? getUserHome = null)
    {
        _error = error;
        _commands = commands;
        _files = files;
        _environment = environment;
        _getUserHome = getUserHome;
    }

    public async Task<UpdateSource?> ResolveSourceAsync(string root, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, stderr) = await _commands.ExecuteAsync(
            "git", ["rev-parse", "HEAD"], root, cancellationToken);
        var hash = stdout.Trim();
        if (exitCode == 0 && IsSourceHash(hash))
            return new UpdateSource(root, hash);

        if (!string.IsNullOrWhiteSpace(stderr))
            _error.WriteLine(stderr.TrimEnd());
        _error.WriteLine("Could not resolve source HEAD from --repo-root. No runtime was changed.");
        return null;
    }

    public async Task<InstalledRuntimeArtifact?> BuildServerAsync(UpdateSource source, CancellationToken cancellationToken)
    {
        var address = CreateAddress(ManagedRuntimeComponent.Server, source.Hash);
        var existing = ReadArtifact(address.Component, address.ComponentRoot, address.VersionRoot);
        if (existing is not null && string.Equals(existing.SourceHash, source.Hash, StringComparison.Ordinal))
            return existing;
        if (_files.Exists(address.VersionRoot))
        {
            _error.WriteLine($"Installed Server artifact '{source.Hash}' is incomplete or tampered; refusing to overwrite it.");
            return null;
        }

        var staging = StagingPath(address.VersionRoot);
        PrepareStaging(staging);
        var (exitCode, stdout, stderr) = await _commands.ExecuteAsync(
            "dotnet",
            [
                "publish",
                "packages/server/src/Mohist.Server/Mohist.Server.csproj",
                "-c",
                "Release",
                "-o",
                staging,
            ],
            source.Root,
            cancellationToken);
        if (exitCode != 0)
        {
            WriteCommandFailure(stdout, stderr);
            _error.WriteLine("Server publish failed. No runtime was changed.");
            RemoveStaging(staging);
            return null;
        }

        var identity = CreateIdentity(ManagedRuntimeComponent.Server, staging, source.Hash);
        if (identity is null)
        {
            RemoveStaging(staging);
            return null;
        }
        await WriteManifestAsync(staging, identity);
        return PromoteStaging(address, staging, identity);
    }

    public async Task<InstalledRuntimeArtifact?> BuildRunnerAsync(UpdateSource source, CancellationToken cancellationToken)
    {
        var address = CreateAddress(ManagedRuntimeComponent.Runner, source.Hash);
        var existing = ReadArtifact(address.Component, address.ComponentRoot, address.VersionRoot);
        if (existing is not null && string.Equals(existing.SourceHash, source.Hash, StringComparison.Ordinal))
            return existing;
        if (_files.Exists(address.VersionRoot))
        {
            _error.WriteLine($"Installed Runner artifact '{source.Hash}' is incomplete or tampered; refusing to overwrite it.");
            return null;
        }

        var staging = StagingPath(address.VersionRoot);
        PrepareStaging(staging);
        var (build, buildOut, buildErr) = await _commands.ExecuteAsync(
            "npm",
            ["run", "build", "-w", "packages/runner"],
            source.Root,
            cancellationToken);
        if (build != 0)
        {
            WriteCommandFailure(buildOut, buildErr);
            _error.WriteLine("Runner build failed. No runtime was changed.");
            RemoveStaging(staging);
            return null;
        }

        var copies = new[]
        {
            (Path.Combine(source.Root, "packages", "runner", "dist"), Path.Combine(staging, "dist")),
            (Path.Combine(source.Root, "packages", "runner", "package.json"), Path.Combine(staging, "package.json")),
            (Path.Combine(source.Root, "node_modules"), Path.Combine(staging, "node_modules")),
        };
        foreach (var (from, to) in copies)
        {
            var (copy, copyOut, copyErr) = await _commands.ExecuteAsync(
                "cp", ["-RL", from, to], source.Root, cancellationToken);
            if (copy == 0)
                continue;

            WriteCommandFailure(copyOut, copyErr);
            _error.WriteLine("Runner artifact install failed. No runtime was changed.");
            RemoveStaging(staging);
            return null;
        }

        var identity = CreateIdentity(ManagedRuntimeComponent.Runner, staging, source.Hash);
        if (identity is null)
        {
            RemoveStaging(staging);
            return null;
        }

        // This generated metadata is intentionally outside the digest payload. It
        // carries the digest that covers the executable payload it accompanies.
        await _files.WriteAllTextAsync(
            Path.Combine(staging, RunnerBuildInfoPath),
            JsonSerializer.Serialize(new { gitHash = source.Hash, artifactDigest = identity.ArtifactDigest }));
        await WriteManifestAsync(staging, identity);
        return PromoteStaging(address, staging, identity);
    }

    public RuntimeActivation Activate(InstalledRuntimeArtifact candidate)
    {
        EnsureArtifactIntegrity(candidate);
        var previousCurrent = ReadLink(candidate.Component, candidate.CurrentLink);
        var previousVerified = ReadLink(candidate.Component, candidate.VerifiedLink);
        var activation = new RuntimeActivation(candidate, previousCurrent, previousVerified);
        _files.ReplaceDirectorySymbolicLink(candidate.CurrentLink, candidate.VersionRoot);
        if (!SameArtifact(ReadLink(candidate.Component, candidate.CurrentLink), candidate))
        {
            throw new RuntimeActivationException(
                activation,
                "current runtime target did not read back as the candidate artifact; installed artifact manifest, payload, or digest did not validate");
        }
        return activation;
    }

    public void MarkVerified(RuntimeActivation activation)
    {
        if (!SameArtifact(ReadLink(activation.Candidate.Component, activation.Candidate.CurrentLink), activation.Candidate))
            throw new InvalidOperationException("candidate no longer owns the current runtime target");
        if (!SameArtifact(ReadLink(activation.Candidate.Component, activation.Candidate.VerifiedLink), activation.PreviousVerified))
            throw new InvalidOperationException("verified runtime target changed during candidate verification");

        _files.ReplaceDirectorySymbolicLink(
            activation.Candidate.VerifiedLink,
            activation.Candidate.VersionRoot);
        if (!SameArtifact(ReadLink(activation.Candidate.Component, activation.Candidate.VerifiedLink), activation.Candidate))
            throw new InvalidOperationException("verified runtime target did not read back as the candidate artifact");
    }

    public bool IsCommitted(RuntimeActivation activation) =>
        SameArtifact(ReadLink(activation.Candidate.Component, activation.Candidate.CurrentLink), activation.Candidate)
        && SameArtifact(ReadLink(activation.Candidate.Component, activation.Candidate.VerifiedLink), activation.Candidate);

    public RuntimeRecovery Restore(RuntimeActivation activation)
    {
        if (!CandidateOwnsCurrentTarget(activation.Candidate))
        {
            return new RuntimeRecovery(
                false,
                null,
                null,
                "candidate no longer owns the current runtime target");
        }

        RestoreLink(activation.Candidate.CurrentLink, activation.PreviousCurrent);
        RestoreLink(activation.Candidate.VerifiedLink, activation.PreviousVerified);

        var restoredCurrent = ReadLink(activation.Candidate.Component, activation.Candidate.CurrentLink);
        var restoredVerified = ReadLink(activation.Candidate.Component, activation.Candidate.VerifiedLink);
        if (!SameArtifact(restoredCurrent, activation.PreviousCurrent)
            || !SameArtifact(restoredVerified, activation.PreviousVerified))
        {
            return new RuntimeRecovery(
                false,
                restoredCurrent,
                restoredVerified,
                "runtime link readback did not match the pre-activation snapshot");
        }

        return new RuntimeRecovery(true, restoredCurrent, restoredVerified);
    }

    public string ResolveComponentRoot(ManagedRuntimeComponent component)
    {
        var home = _getUserHome?.Invoke();
        if (string.IsNullOrWhiteSpace(home))
            home = _environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "mohist", "runtime", ComponentName(component));
    }

    private InstalledRuntimeArtifact CreateAddress(ManagedRuntimeComponent component, string sourceHash)
    {
        var componentRoot = ResolveComponentRoot(component);
        return new InstalledRuntimeArtifact(
            component,
            sourceHash,
            componentRoot,
            Path.Combine(componentRoot, "versions", sourceHash),
            string.Empty,
            EntryPointFor(component));
    }

    private InstalledRuntimeArtifact? PromoteStaging(
        InstalledRuntimeArtifact address,
        string staging,
        RuntimeArtifactIdentity identity)
    {
        if (_files.Exists(address.VersionRoot))
        {
            _error.WriteLine($"Installed {ComponentName(address.Component)} artifact '{address.SourceHash}' is incomplete; refusing to overwrite it.");
            RemoveStaging(staging);
            return null;
        }

        try
        {
            _files.CreateDirectory(Path.GetDirectoryName(address.VersionRoot)!);
            _files.Move(staging, address.VersionRoot);
            var artifact = address with
            {
                ArtifactDigest = identity.ArtifactDigest,
                EntryPoint = identity.EntryPoint,
            };
            EnsureArtifactIntegrity(artifact);
            return artifact;
        }
        catch (Exception ex)
        {
            _error.WriteLine($"Could not promote installed {ComponentName(address.Component)} artifact: {ex.Message}");
            RemoveStaging(staging);
            return null;
        }
    }

    private InstalledRuntimeArtifact? ReadLink(ManagedRuntimeComponent component, string linkPath)
    {
        var componentRoot = ResolveComponentRoot(component);
        var target = _files.ReadDirectorySymbolicLink(linkPath);
        if (string.IsNullOrWhiteSpace(target))
            return null;

        var versionsRoot = Path.GetFullPath(Path.Combine(componentRoot, "versions"));
        var resolvedTarget = Path.GetFullPath(target);
        if (!resolvedTarget.StartsWith(versionsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(resolvedTarget, versionsRoot, StringComparison.Ordinal))
        {
            _error.WriteLine($"Managed {ComponentName(component)} target is outside the versions directory; refusing to use it.");
            return null;
        }

        return ReadArtifact(component, componentRoot, resolvedTarget);
    }

    private InstalledRuntimeArtifact? ReadArtifact(
        ManagedRuntimeComponent component,
        string componentRoot,
        string versionRoot)
    {
        var identity = ReadAndValidateIdentity(component, versionRoot);
        return identity is null
            ? null
            : new InstalledRuntimeArtifact(
                component,
                identity.SourceHash,
                componentRoot,
                versionRoot,
                identity.ArtifactDigest,
                identity.EntryPoint);
    }

    private RuntimeArtifactIdentity? CreateIdentity(
        ManagedRuntimeComponent component,
        string root,
        string sourceHash)
    {
        var entryPoint = EntryPointFor(component);
        var entryPointPath = Path.Combine(root, entryPoint);
        if (!_files.Exists(entryPointPath))
        {
            _error.WriteLine($"Installed {ComponentName(component)} artifact is missing required entry point '{entryPoint}'.");
            return null;
        }

        var payloadFiles = PayloadFiles(component, root);
        var digest = ComputePayloadDigest(root, payloadFiles);
        return new RuntimeArtifactIdentity(sourceHash, digest, entryPoint, payloadFiles);
    }

    private RuntimeArtifactIdentity? ReadAndValidateIdentity(ManagedRuntimeComponent component, string root)
    {
        var manifest = ReadManifest(Path.Combine(root, ManifestFileName));
        if (manifest is null
            || !IsSourceHash(manifest.SourceHash)
            || !IsDigest(manifest.ArtifactDigest)
            || !string.Equals(manifest.EntryPoint, EntryPointFor(component), StringComparison.Ordinal)
            || !_files.Exists(Path.Combine(root, manifest.EntryPoint)))
        {
            return null;
        }

        var actualFiles = PayloadFiles(component, root);
        if (!actualFiles.SequenceEqual(manifest.PayloadFiles, StringComparer.Ordinal))
            return null;

        var digest = ComputePayloadDigest(root, actualFiles);
        if (!string.Equals(digest, manifest.ArtifactDigest, StringComparison.Ordinal))
            return null;

        if (component == ManagedRuntimeComponent.Runner
            && !RunnerBuildInfoMatches(root, manifest.SourceHash, manifest.ArtifactDigest))
        {
            return null;
        }

        return manifest;
    }

    private void EnsureArtifactIntegrity(InstalledRuntimeArtifact artifact)
    {
        var actual = ReadArtifact(artifact.Component, artifact.ComponentRoot, artifact.VersionRoot);
        if (!SameArtifact(actual, artifact))
            throw new InvalidOperationException("installed artifact manifest, payload, or digest did not validate");
    }

    private IReadOnlyList<string> PayloadFiles(ManagedRuntimeComponent component, string root)
    {
        return _files.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !string.Equals(path, ManifestFileName, StringComparison.Ordinal)
                && !(component == ManagedRuntimeComponent.Runner
                    && string.Equals(path, RunnerBuildInfoPath, StringComparison.Ordinal)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private string ComputePayloadDigest(string root, IReadOnlyList<string> payloadFiles)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in payloadFiles)
        {
            var pathBytes = Encoding.UTF8.GetBytes(relativePath);
            AppendUInt64(hash, (ulong)pathBytes.Length);
            hash.AppendData(pathBytes);

            using var payload = _files.OpenRead(Path.Combine(root, relativePath));
            AppendPayloadDigest(hash, payload);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendPayloadDigest(IncrementalHash manifestHash, Stream payload)
    {
        using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        ulong length = 0;
        int read;
        while ((read = payload.Read(buffer, 0, buffer.Length)) > 0)
        {
            payloadHash.AppendData(buffer, 0, read);
            length += (uint)read;
        }

        AppendUInt64(manifestHash, length);
        manifestHash.AppendData(payloadHash.GetHashAndReset());
    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private async Task WriteManifestAsync(string root, RuntimeArtifactIdentity identity) =>
        await _files.WriteAllTextAsync(
            Path.Combine(root, ManifestFileName),
            JsonSerializer.Serialize(new
            {
                gitHash = identity.SourceHash,
                artifactDigest = identity.ArtifactDigest,
                entryPoint = identity.EntryPoint,
                payloadFiles = identity.PayloadFiles,
            }));

    private RuntimeArtifactIdentity? ReadManifest(string path)
    {
        try
        {
            if (!_files.Exists(path))
                return null;
            using var document = JsonDocument.Parse(_files.ReadAllText(path));
            var root = document.RootElement;
            if (!TryGetString(root, "gitHash", out var sourceHash)
                || !TryGetString(root, "artifactDigest", out var artifactDigest)
                || !TryGetString(root, "entryPoint", out var entryPoint)
                || !root.TryGetProperty("payloadFiles", out var files)
                || files.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var payloadFiles = new List<string>();
            foreach (var file in files.EnumerateArray())
            {
                var payloadFile = file.ValueKind == JsonValueKind.String ? file.GetString() : null;
                if (string.IsNullOrWhiteSpace(payloadFile))
                    return null;
                payloadFiles.Add(payloadFile);
            }
            if (!payloadFiles.SequenceEqual(payloadFiles.OrderBy(path => path, StringComparer.Ordinal), StringComparer.Ordinal)
                || payloadFiles.Distinct(StringComparer.Ordinal).Count() != payloadFiles.Count)
            {
                return null;
            }
            return new RuntimeArtifactIdentity(sourceHash, artifactDigest, entryPoint, payloadFiles);
        }
        catch
        {
            return null;
        }
    }

    private bool RunnerBuildInfoMatches(string root, string sourceHash, string artifactDigest)
    {
        try
        {
            var path = Path.Combine(root, RunnerBuildInfoPath);
            if (!_files.Exists(path))
                return false;
            using var document = JsonDocument.Parse(_files.ReadAllText(path));
            return TryGetString(document.RootElement, "gitHash", out var reportedHash)
                && TryGetString(document.RootElement, "artifactDigest", out var reportedDigest)
                && string.Equals(reportedHash, sourceHash, StringComparison.Ordinal)
                && string.Equals(reportedDigest, artifactDigest, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private void RestoreLink(string linkPath, InstalledRuntimeArtifact? target)
    {
        if (target is null)
        {
            _files.DeleteDirectorySymbolicLink(linkPath);
            return;
        }
        _files.ReplaceDirectorySymbolicLink(linkPath, target.VersionRoot);
    }

    private bool CandidateOwnsCurrentTarget(InstalledRuntimeArtifact candidate)
    {
        var target = _files.ReadDirectorySymbolicLink(candidate.CurrentLink);
        return !string.IsNullOrWhiteSpace(target)
            && string.Equals(
                Path.GetFullPath(target),
                Path.GetFullPath(candidate.VersionRoot),
                StringComparison.Ordinal);
    }

    private static bool SameArtifact(InstalledRuntimeArtifact? left, InstalledRuntimeArtifact? right) =>
        left is null && right is null
        || left is not null && right is not null
            && left.Component == right.Component
            && string.Equals(left.SourceHash, right.SourceHash, StringComparison.Ordinal)
            && string.Equals(left.ArtifactDigest, right.ArtifactDigest, StringComparison.Ordinal)
            && string.Equals(left.VersionRoot, right.VersionRoot, StringComparison.Ordinal);

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var candidate)
            || candidate.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = candidate.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool IsSourceHash(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(c => (c is >= 'a' and <= 'z')
            || (c is >= 'A' and <= 'Z')
            || (c is >= '0' and <= '9'));

    private static bool IsDigest(string? value) =>
        value is { Length: 64 }
        && value.All(c => (c is >= 'a' and <= 'f') || (c is >= '0' and <= '9'));

    private static string ComponentName(ManagedRuntimeComponent component) => component switch
    {
        ManagedRuntimeComponent.Server => "server",
        ManagedRuntimeComponent.Runner => "runner",
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    private static string EntryPointFor(ManagedRuntimeComponent component) => component switch
    {
        ManagedRuntimeComponent.Server => "Mohist.Server.dll",
        ManagedRuntimeComponent.Runner => "dist/cli.js",
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    private static string StagingPath(string versionRoot) =>
        versionRoot + ".staging-" + Guid.NewGuid().ToString("N");

    private void PrepareStaging(string staging)
    {
        if (_files.Exists(staging))
            _files.DeleteDirectory(staging);
        _files.CreateDirectory(staging);
    }

    private void RemoveStaging(string staging)
    {
        try
        {
            if (_files.Exists(staging))
                _files.DeleteDirectory(staging);
        }
        catch
        {
        }
    }

    private void WriteCommandFailure(string stdout, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stdout))
            _error.WriteLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr))
            _error.WriteLine(stderr.TrimEnd());
    }
}
