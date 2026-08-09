using System.Text.Json;

namespace Mohist.Cli;

internal enum ManagedRuntimeComponent
{
    Server,
    Runner,
}

internal sealed record UpdateSource(string Root, string Hash);

internal sealed record InstalledRuntimeArtifact(
    ManagedRuntimeComponent Component,
    string SourceHash,
    string ComponentRoot,
    string VersionRoot)
{
    public string CurrentLink => Path.Combine(ComponentRoot, "current");
    public string VerifiedLink => Path.Combine(ComponentRoot, "verified");
}

internal sealed record RuntimeActivation(
    InstalledRuntimeArtifact Candidate,
    InstalledRuntimeArtifact? PreviousVerified);

internal sealed record PreparedRuntimeUpdate(
    UpdateSource Source,
    RuntimeActivation Activation);

/// <summary>
/// Owns the source-to-installed-artifact boundary for managed Server and Runner services.
/// A service may only execute a version under this root; source checkouts are build inputs.
/// </summary>
internal sealed class InstalledRuntimeArtifacts
{
    private const string ManifestFileName = "mohist-build.json";

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
        var artifact = CreateArtifact(ManagedRuntimeComponent.Server, source.Hash);
        if (IsComplete(artifact))
            return artifact;

        var staging = StagingPath(artifact);
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

        await WriteManifestAsync(staging, source.Hash);
        return PromoteStaging(artifact, staging);
    }

    public async Task<InstalledRuntimeArtifact?> BuildRunnerAsync(UpdateSource source, CancellationToken cancellationToken)
    {
        var artifact = CreateArtifact(ManagedRuntimeComponent.Runner, source.Hash);
        if (IsComplete(artifact))
            return artifact;

        var staging = StagingPath(artifact);
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

        // The generated file is the identity the runner reports after reconnecting.
        await _files.WriteAllTextAsync(
            Path.Combine(staging, "dist", "build-info.json"),
            JsonSerializer.Serialize(new { gitHash = source.Hash }));
        await WriteManifestAsync(staging, source.Hash);
        return PromoteStaging(artifact, staging);
    }

    public RuntimeActivation Activate(InstalledRuntimeArtifact candidate)
    {
        var previous = ReadVerified(candidate.Component);
        _files.ReplaceDirectorySymbolicLink(candidate.CurrentLink, candidate.VersionRoot);
        return new RuntimeActivation(candidate, previous);
    }

    public void MarkVerified(RuntimeActivation activation) =>
        _files.ReplaceDirectorySymbolicLink(
            activation.Candidate.VerifiedLink,
            activation.Candidate.VersionRoot);

    public bool Restore(RuntimeActivation activation)
    {
        if (activation.PreviousVerified is null)
        {
            _files.DeleteDirectorySymbolicLink(activation.Candidate.CurrentLink);
            return false;
        }

        _files.ReplaceDirectorySymbolicLink(
            activation.Candidate.CurrentLink,
            activation.PreviousVerified.VersionRoot);
        return true;
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

    private InstalledRuntimeArtifact CreateArtifact(ManagedRuntimeComponent component, string sourceHash)
    {
        var componentRoot = ResolveComponentRoot(component);
        return new InstalledRuntimeArtifact(
            component,
            sourceHash,
            componentRoot,
            Path.Combine(componentRoot, "versions", sourceHash));
    }

    private bool IsComplete(InstalledRuntimeArtifact artifact)
    {
        var manifest = ReadManifestHash(Path.Combine(artifact.VersionRoot, ManifestFileName));
        return string.Equals(manifest, artifact.SourceHash, StringComparison.Ordinal);
    }

    private InstalledRuntimeArtifact? PromoteStaging(InstalledRuntimeArtifact artifact, string staging)
    {
        if (_files.Exists(artifact.VersionRoot))
        {
            _error.WriteLine($"Installed {ComponentName(artifact.Component)} artifact '{artifact.SourceHash}' is incomplete; refusing to overwrite it.");
            RemoveStaging(staging);
            return null;
        }

        try
        {
            _files.CreateDirectory(Path.GetDirectoryName(artifact.VersionRoot)!);
            _files.Move(staging, artifact.VersionRoot);
            return artifact;
        }
        catch (Exception ex)
        {
            _error.WriteLine($"Could not promote installed {ComponentName(artifact.Component)} artifact: {ex.Message}");
            RemoveStaging(staging);
            return null;
        }
    }

    private InstalledRuntimeArtifact? ReadVerified(ManagedRuntimeComponent component)
    {
        var componentRoot = ResolveComponentRoot(component);
        var target = _files.ReadDirectorySymbolicLink(Path.Combine(componentRoot, "verified"));
        if (string.IsNullOrWhiteSpace(target))
            return null;

        var versionsRoot = Path.GetFullPath(Path.Combine(componentRoot, "versions"));
        var resolvedTarget = Path.GetFullPath(target);
        if (!resolvedTarget.StartsWith(versionsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(resolvedTarget, versionsRoot, StringComparison.Ordinal))
        {
            _error.WriteLine($"Verified {ComponentName(component)} target is outside the managed versions directory; refusing to restore it.");
            return null;
        }

        var hash = ReadManifestHash(Path.Combine(resolvedTarget, ManifestFileName));
        if (hash is not { } verifiedHash || !IsSourceHash(verifiedHash))
            return null;

        return new InstalledRuntimeArtifact(component, verifiedHash, componentRoot, resolvedTarget);
    }

    private static bool IsSourceHash(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(c => (c is >= 'a' and <= 'z')
            || (c is >= 'A' and <= 'Z')
            || (c is >= '0' and <= '9'));

    private static string ComponentName(ManagedRuntimeComponent component) => component switch
    {
        ManagedRuntimeComponent.Server => "server",
        ManagedRuntimeComponent.Runner => "runner",
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    private static string StagingPath(InstalledRuntimeArtifact artifact) =>
        artifact.VersionRoot + ".staging-" + Guid.NewGuid().ToString("N");

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

    private async Task WriteManifestAsync(string root, string sourceHash) =>
        await _files.WriteAllTextAsync(
            Path.Combine(root, ManifestFileName),
            JsonSerializer.Serialize(new { gitHash = sourceHash }));

    private string? ReadManifestHash(string path)
    {
        try
        {
            if (!_files.Exists(path))
                return null;
            using var document = JsonDocument.Parse(_files.ReadAllText(path));
            return document.RootElement.TryGetProperty("gitHash", out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
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
